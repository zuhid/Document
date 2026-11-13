# nginx configuration cheatsheet

A quick reference for writing and debugging nginx configs, with nginx running in a Docker container (official `nginx` image). Replace `<container>` with your container's name.

## Running the container

```sh
# Mount a single server config (read-only) and publish a port
docker run --name <container> --detach -p 8080:80 \
  -v "$PWD/default.conf:/etc/nginx/conf.d/default.conf:ro" \
  nginx:alpine

# Mount a whole directory of configs instead
docker run --name <container> --detach -p 8080:80 \
  -v "$PWD/conf.d:/etc/nginx/conf.d:ro" \
  nginx:alpine

# Serve static files
docker run --name <container> --detach -p 8080:80 \
  -v "$PWD/dist:/usr/share/nginx/html:ro" \
  nginx:alpine

docker container rm <container> --force   # stop and remove
```

- `-p host:container` maps ports; the `listen` port in the config is the **container** port.
- `--network host` (Linux only) shares the host's network: no `-p` needed, and `127.0.0.1` in the config means the host.
- Mounting a single file pins its inode: editors that save by replacing the file can leave the container seeing the old version. Mount the directory instead, or restart the container.

## Commands

```sh
docker exec <container> nginx -t          # test config syntax (always run before reload)
docker exec <container> nginx -T          # test AND print the full merged config
docker exec <container> nginx -s reload   # graceful reload: new workers get the new config
docker exec <container> nginx -V          # version + compiled-in modules and paths
docker logs -f <container>                # access + error logs
docker restart <container>                # full restart (needed after changing mounts)

# Validate a config without touching the running container
docker run --rm -v "$PWD/default.conf:/etc/nginx/conf.d/default.conf:ro" nginx:alpine nginx -t
```

## File layout (official Docker image)

| Path | Purpose |
| --- | --- |
| `/etc/nginx/nginx.conf` | Main file: `events`, `http`, global settings |
| `/etc/nginx/conf.d/*.conf` | Included inside `http {}`; put `server` blocks here |
| `/etc/nginx/templates/*.template` | Rendered with `envsubst` into `conf.d/` at container start |
| `/etc/nginx/mime.types` | Extension → `Content-Type` map |
| `/var/log/nginx/` | Logs; symlinked to stdout/stderr, so use `docker logs` |
| `/usr/share/nginx/html` | Default static root |

Only files ending in `.conf` in `conf.d/` are auto-included. Name shared snippets something else (e.g. `proxy_headers.inc`) and `include` them explicitly, so they are never loaded on their own at `http` level.

### Environment variables in config

nginx config has no env var support, but the image renders templates at startup:

```sh
docker run --name <container> --detach -p 8080:80 \
  -e API_HOST=api -e API_PORT=8080 \
  -v "$PWD/default.conf.template:/etc/nginx/templates/default.conf.template:ro" \
  nginx:alpine
```

```nginx
# default.conf.template → /etc/nginx/conf.d/default.conf
location /api/ {
  proxy_pass http://${API_HOST}:${API_PORT}/;
}
```

Only variables that are actually set in the environment are substituted, so nginx's own variables like `$host` are left alone.

### Reaching other services from the container

| Upstream runs... | Use in `proxy_pass` / `upstream` |
| --- | --- |
| In another container on the same Docker network | The container/service name, e.g. `http://api:8080` |
| On the host (Docker Desktop) | `host.docker.internal` |
| On the host (Linux) | `--add-host=host.docker.internal:host-gateway`, or `--network host` + `127.0.0.1` |

`127.0.0.1` inside a normal container is the container itself, not the host. A common cause of 502.

If an upstream container can be recreated with a new IP, nginx keeps the IP it resolved at startup. Use Docker's DNS with a variable to re-resolve:

```nginx
resolver 127.0.0.11 valid=10s;   # Docker's embedded DNS
set $api http://api:8080;
proxy_pass $api;                 # with a variable, the URI is NOT rewritten (see proxy_pass rules)
```

## Structure

```nginx
user  nginx;
worker_processes auto;          # main context

events {
  worker_connections 1024;
}

http {                          # HTTP settings shared by all servers
  include mime.types;

  upstream backend { ... }      # named group of backend servers

  server {                      # one virtual host
    listen 80;
    server_name example.com;

    location / { ... }          # rules for a URI pattern
  }
}
```

Directives inherit downward (`http` → `server` → `location`). A child that sets the same directive **replaces** the parent's value; for array-like directives such as `proxy_set_header` and `add_header`, defining even one in a child drops *all* inherited ones.

## server blocks

```nginx
server {
  listen 80;                          # IPv4
  listen [::]:80;                     # IPv6
  listen 443 ssl;                     # TLS (http2 on; in newer versions)
  listen 8080 default_server;         # catches requests no server_name matches

  server_name example.com www.example.com;
  server_name *.example.com;          # wildcard
  server_name ~^(?<sub>.+)\.example\.com$;   # regex, captures $sub
  server_name _;                      # convention for "no real name"
}
```

Server selection: first by `listen` address/port, then by `Host` header against `server_name`. If nothing matches, the `default_server` (or the first server for that port) wins.

## location matching

| Syntax | Meaning | Example |
| --- | --- | --- |
| `location = /path` | Exact match. Checked first; stops immediately | `location = /api` |
| `location ^~ /path` | Prefix; if longest, skip regex checks | `location ^~ /static/` |
| `location ~ regex` | Case-sensitive regex | `location ~ \.php$` |
| `location ~* regex` | Case-insensitive regex | `location ~* \.(jpg\|png)$` |
| `location /path` | Plain prefix | `location /api/` |
| `location @name` | Named; only reachable via `try_files`/`error_page` | `location @fallback` |

**Order of evaluation**

1. Exact `=` match → use it.
2. Find the longest prefix match. If it is `^~` → use it.
3. Test regex locations in file order; the first match wins.
4. Otherwise use the longest prefix match from step 2.

Matching runs against the **normalized** URI: `%XX` decoded, `/./` and `/../` resolved, repeated slashes merged. Query strings are never part of it.

Note on trailing slashes: `location /api/` does not match `/api`, and `location /api` also matches `/apix`. To match exactly `/api` and everything under it, use two locations: `= /api` plus `/api/`.

## Reverse proxy

```nginx
upstream api {
  server api:8080;                    # container name on the same Docker network
  server api-backup:8080 backup;      # used only when the others are down
  # least_conn;  ip_hash;             # balancing method (default round-robin)
  keepalive 16;                       # reuse upstream connections
}

location /api/ {
  proxy_pass http://api;              # see the table below for URI rules
  proxy_http_version 1.1;             # needed for keepalive and WebSockets
  proxy_set_header Connection "";     # needed for upstream keepalive

  proxy_set_header Host $host;
  proxy_set_header X-Real-IP $remote_addr;
  proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
  proxy_set_header X-Forwarded-Proto $scheme;

  proxy_connect_timeout 5s;
  proxy_read_timeout 60s;             # max gap between two reads from upstream
  proxy_send_timeout 60s;
}
```

### proxy_pass URI rules (the one everyone gets wrong)

For `location /api/` and a request to `/api/users`:

| `proxy_pass` | Upstream receives | Why |
| --- | --- | --- |
| `http://api` | `/api/users` | No URI: the **original** request URI is passed unchanged |
| `http://api/` | `/users` | URI given: the matched prefix `/api/` is replaced by `/` |
| `http://api/v2/` | `/v2/users` | Prefix `/api/` replaced by `/v2/` |
| `http://api/v2` | `/v2users` | Missing slash: prefix replaced by `/v2` with nothing in between |

With a URI part, nginx sends the **normalized** URI; without one, it sends the raw URI as the client wrote it. When the proxy is meant to expose only some paths, use a URI part: `/api/../admin` is then resolved to `/admin` by nginx and can be blocked, instead of reaching the backend as-is.

If `proxy_pass` contains a variable, nginx does not do the prefix replacement; build the full URI yourself (e.g. `proxy_pass http://$backend$request_uri;`).

### WebSockets

```nginx
map $http_upgrade $connection_upgrade {   # in http {}
  default upgrade;
  ''      close;
}

location /ws/ {
  proxy_pass http://api;
  proxy_http_version 1.1;
  proxy_set_header Upgrade $http_upgrade;
  proxy_set_header Connection $connection_upgrade;
  proxy_read_timeout 1h;
}
```

### Buffering and uploads

```nginx
client_max_body_size 30m;         # default 1m; 0 = unlimited; too big → 413
proxy_request_buffering off;      # stream request body to upstream as it arrives
proxy_buffering off;              # stream response to client (downloads, SSE)
client_body_timeout 60s;
```

## Static files and SPAs

```nginx
server {
  root /usr/share/nginx/html;     # file path = root + URI
  index index.html;

  location / {
    try_files $uri $uri/ /index.html;   # SPA fallback (e.g. a Vite/Vue build)
  }

  location /assets/ {
    expires 1y;
    add_header Cache-Control "public, immutable";
  }

  location /files/ {
    alias /data/shared/;          # path = alias + (URI minus "/files/")
    autoindex on;                 # directory listing
  }
}
```

`root` appends the whole URI; `alias` replaces the matched location prefix. With `alias`, keep the trailing slashes consistent on both sides.

## Returning responses and redirects

```nginx
return 404;
return 200 "ok\n";                          # add default_type text/plain;
return 301 https://$host$request_uri;       # permanent redirect
return 302 /login;                          # temporary redirect

rewrite ^/old/(.*)$ /new/$1 permanent;      # 301 via regex
rewrite ^/v1/(.*)$ /v2/$1 last;             # internal rewrite, re-run location matching
rewrite ^/x/(.*)$ /y/$1 break;              # internal rewrite, stay in this location

error_page 404 /404.html;
error_page 502 503 504 /50x.html;
```

Prefer `return` over `rewrite` when no regex capture is needed.

## HTTPS

```nginx
server {
  listen 80;
  server_name example.com;
  return 301 https://$host$request_uri;
}

server {
  listen 443 ssl;
  http2 on;                                   # nginx 1.25.1+
  server_name example.com;

  ssl_certificate     /etc/ssl/certs/fullchain.pem;
  ssl_certificate_key /etc/ssl/private/privkey.pem;
  ssl_protocols TLSv1.2 TLSv1.3;
  ssl_session_cache shared:SSL:10m;

  add_header Strict-Transport-Security "max-age=31536000" always;
}
```

## Headers, CORS and security

```nginx
add_header X-Content-Type-Options nosniff always;   # "always" = also on 4xx/5xx
add_header X-Frame-Options DENY always;
server_tokens off;                                  # hide nginx version

# Hide or override a header from the upstream
proxy_hide_header X-Powered-By;

# CORS (only if the backend does NOT already handle it, or headers get duplicated)
location /api/ {
  if ($request_method = OPTIONS) {
    add_header Access-Control-Allow-Origin $http_origin;
    add_header Access-Control-Allow-Methods "GET, POST, PUT, DELETE";
    add_header Access-Control-Allow-Headers "Content-Type, Authorization";
    return 204;
  }
  add_header Access-Control-Allow-Origin $http_origin always;
  proxy_pass http://api;
}
```

## Access control and rate limiting

```nginx
location /admin/ {
  allow 10.0.0.0/8;
  allow 127.0.0.1;
  deny  all;                          # first matching rule wins

  auth_basic "Restricted";
  auth_basic_user_file /etc/nginx/.htpasswd;   # htpasswd -c .htpasswd user
}

# In http {}
limit_req_zone  $binary_remote_addr zone=perip:10m rate=10r/s;
limit_conn_zone $binary_remote_addr zone=conns:10m;

# In server {} or location {}
limit_req  zone=perip burst=20 nodelay;   # excess → 503 (change with limit_req_status 429;)
limit_conn conns 10;
```

## Compression

```nginx
gzip on;
gzip_types text/plain text/css application/json application/javascript image/svg+xml;
gzip_min_length 1024;
gzip_proxied any;                   # also compress proxied responses
```

## Logging

```nginx
log_format main '$remote_addr "$request" $status $body_bytes_sent '
                'rt=$request_time urt=$upstream_response_time';

access_log /var/log/nginx/access.log main;
access_log off;                       # e.g. inside a health-check location
error_log  /var/log/nginx/error.log warn;   # debug | info | notice | warn | error | crit
```

## Useful variables

| Variable | Value |
| --- | --- |
| `$host` | `Host` header (or `server_name` if missing), no port |
| `$http_<name>` | Any request header, e.g. `$http_user_agent`, `$http_origin` |
| `$uri` | Normalized URI, no query string (changes with rewrites) |
| `$request_uri` | Original raw URI **with** query string |
| `$args` / `$arg_<name>` | Whole query string / one parameter |
| `$request_method` | `GET`, `POST`, ... |
| `$scheme` | `http` or `https` |
| `$remote_addr` | Client IP (or the previous proxy) |
| `$proxy_add_x_forwarded_for` | Incoming `X-Forwarded-For` + `$remote_addr` |
| `$status` | Response status (for logs) |
| `$upstream_addr` | Which backend handled the request |

## map and if

```nginx
# map: in http {}, evaluated lazily; the clean way to do conditionals
map $http_origin $cors_origin {
  default "";
  "https://app.example.com" $http_origin;
}

# if: fine for return/rewrite, unreliable for most other directives ("if is evil")
if ($http_user_agent ~* "badbot") { return 403; }
```

## Common errors

| Symptom | Likely cause |
| --- | --- |
| `502 Bad Gateway` | Upstream not running, wrong port, or unreachable from the container (`127.0.0.1` is the container itself; check the Docker network) |
| `504 Gateway Timeout` | Upstream too slow; raise `proxy_read_timeout` |
| `413 Request Entity Too Large` | Body exceeds `client_max_body_size` |
| `404` from nginx instead of the app | No `location` matched, or `proxy_pass` URI rewrite dropped/doubled a prefix |
| Wrong path at the backend | Trailing slash mismatch in `location` vs `proxy_pass` |
| Redirects point to `localhost` or wrong port | Missing `proxy_set_header Host`, or backend ignores `X-Forwarded-*` |
| Headers from `http {}` vanished | A child block defined its own `add_header`/`proxy_set_header` |
| `host not found in upstream` at startup | Upstream hostname not resolvable when nginx starts |
| Container exits immediately | Syntax error or port in use; check `docker logs <container>` |
| Config change has no effect | Forgot `nginx -s reload`, or a single-file mount still points at the old file |
| 502 after an upstream container restarts | nginx cached the old IP; restart nginx or use `resolver` + a variable |
