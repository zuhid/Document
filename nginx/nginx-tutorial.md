# Learning nginx: a hands-on tutorial

Step-by-step lessons for an experienced developer who is new to nginx. Everything runs in Docker, so nothing is installed on your machine and each lesson is a small config change you can see the effect of with `curl`.

Each lesson has a **goal**, the **config** to write, **commands** to verify it, and **takeaways**. Do them in order; later lessons build on earlier ones. The companion [nginx.md](nginx.md) is the reference cheatsheet once you know the concepts.

**Prerequisites:** Docker, `curl`, `openssl`. About 2–3 hours for the whole thing.

---

## Lesson 0: The mental model

Before touching config, these few ideas explain most of nginx's behavior:

- **nginx is configured, not programmed.** The config is a declarative tree of *directives*. There is no execution order in the sense of a script: nginx picks *one* `server` block and then *one* `location` block for each request, and the directives in that location (plus inherited ones) describe how to handle it.
- **Contexts nest:** `main` → `events` / `http` → `server` → `location`. Most directives are allowed only in certain contexts; the docs list them under "Context:" for every directive.
- **Inheritance is by replacement.** A setting in an outer block applies inside unless the inner block sets the same directive, in which case the inner one *replaces* it entirely. This bites hardest with `proxy_set_header` and `add_header`.
- **Process model:** one master process reads config and manages worker processes; workers are single-threaded event loops handling thousands of connections each. A reload starts new workers with the new config and gracefully drains the old ones: zero downtime.
- **Analogies:** a `server` block is a virtual host; a `location` is a route; an `upstream` is a load-balanced backend pool; `proxy_pass` is "forward this request there".

---

## Lesson 1: Set up the lab

**Goal:** a running nginx container whose config you edit on your host, plus a fast edit → test → reload loop.

```sh
mkdir -p ~/nginx-lab/{conf.d,html,certs,templates} && cd ~/nginx-lab
docker network create nginx-lab
```

First, look at what the image ships with. Reading the defaults is the quickest way to see real config:

```sh
docker run --rm nginx:alpine cat /etc/nginx/nginx.conf
docker run --rm nginx:alpine cat /etc/nginx/conf.d/default.conf
```

Notice in `nginx.conf`: `include /etc/nginx/conf.d/*.conf;` sits **inside** `http {}`. Every file you put in `conf.d/` is therefore in the `http` context. That is where your `server` blocks go, and also `upstream`, `map`, `log_format` and similar http-level directives (written outside any `server` block in the same file).

Create a minimal config, `conf.d/default.conf`:

```nginx
server {
  listen 80;
  default_type text/plain;

  location / {
    return 200 "hello from nginx\n";
  }
}
```

Start the container. Mount **directories**, not single files, so edits on the host always show up inside:

```sh
docker run -d --name web --network nginx-lab \
  -p 8080:80 -p 8443:443 \
  -v "$PWD/conf.d:/etc/nginx/conf.d:ro" \
  -v "$PWD/html:/usr/share/nginx/html:ro" \
  -v "$PWD/certs:/etc/nginx/certs:ro" \
  nginx:alpine

curl localhost:8080
```

Set up the reload loop you will use in every lesson:

```sh
alias ngx-reload='docker exec web nginx -t && docker exec web nginx -s reload'
alias ngx-logs='docker logs -f web'
```

`nginx -t` validates the config; only if it passes do you reload. A failed reload keeps the old config running, so a typo never takes the server down, but you have to check the output to notice it failed.

**Try:** break the config (remove a `;`), run `ngx-reload`, read the error (it names the file and line), fix it.

**Takeaways:** config lives in `conf.d/*.conf` inside `http {}`; always `nginx -t` before reload; reload is graceful.

---

## Lesson 2: Server blocks and virtual hosts

**Goal:** understand how nginx chooses a `server` block.

Replace `conf.d/default.conf`:

```nginx
server {
  listen 80 default_server;
  default_type text/plain;
  return 200 "default server, Host=$host\n";
}

server {
  listen 80;
  server_name app.local;
  default_type text/plain;
  return 200 "app.local\n";
}

server {
  listen 80;
  server_name *.api.local;
  default_type text/plain;
  return 200 "wildcard api, Host=$host\n";
}
```

```sh
ngx-reload
curl localhost:8080                              # default server
curl -H "Host: app.local" localhost:8080         # app.local
curl -H "Host: v1.api.local" localhost:8080      # wildcard api
curl -H "Host: unknown.com" localhost:8080       # default server
```

**Takeaways:** nginx first filters servers by `listen` address/port, then matches the `Host` header against `server_name`. No match → the `default_server` (or the first server for that port if none is marked). Production setups usually add a default server that returns `444` (close connection) to drop requests for unknown hosts.

---

## Lesson 3: Location matching

**Goal:** be able to predict which `location` handles any URI. This is the most important lesson.

```nginx
server {
  listen 80;
  default_type text/plain;

  location = /exact          { return 200 "1: exact =\n"; }
  location ^~ /images/       { return 200 "2: prefix ^~ /images/\n"; }
  location /images/big/      { return 200 "3: prefix /images/big/\n"; }
  location ~* \.(png|jpg)$   { return 200 "4: regex ~* png|jpg\n"; }
  location /                 { return 200 "5: prefix /  (uri=$uri)\n"; }
}
```

**Before running each command, predict the answer:**

```sh
curl localhost:8080/exact            # ?
curl localhost:8080/exact/           # ?
curl localhost:8080/images/a.png     # ?
curl localhost:8080/images/big/a.png # ?
curl localhost:8080/images/big/a.txt # ?
curl localhost:8080/docs/A.PNG       # ?
curl --path-as-is localhost:8080/x/../exact     # ?
curl localhost:8080/%65xact          # ?  (%65 is "e")
```

<details>
<summary>Answers</summary>

1. `1` – exact match.
2. `5` – exact means exact; the trailing slash doesn't match.
3. `2` – longest prefix is `/images/`, marked `^~`, so regexes are skipped.
4. `4` – longest prefix is `/images/big/` (no `^~`), so regexes are checked and `\.png$` wins.
5. `3` – regexes checked, none match, fall back to the longest prefix.
6. `4` – `~*` is case-insensitive.
7. `1` – nginx normalizes the URI (resolves `..`) before matching.
8. `1` – percent-encoding is decoded before matching.

</details>

The algorithm:

1. Exact `=` match → done.
2. Remember the longest matching prefix. If it has `^~` → done.
3. Check regex locations **in file order**; the first match wins.
4. Otherwise use the remembered prefix.

**Exercise:** add `location /api` (no trailing slash) and check `curl localhost:8080/apix`. Then fix it so only `/api` and `/api/...` match (hint: two locations).

**Takeaways:** prefixes are chosen by length, regexes by order; `^~` disables regexes for that prefix; matching uses the decoded, normalized URI and never the query string.

---

## Lesson 4: Serving static files

**Goal:** serve files and understand `root`, `alias`, `index` and `try_files`.

```sh
mkdir -p html/assets html/docs
echo '<h1>home</h1>'     > html/index.html
echo 'body{}'            > html/assets/site.css
echo 'readme'            > html/docs/readme.txt
```

```nginx
server {
  listen 80;
  root /usr/share/nginx/html;
  index index.html;

  location / {
    try_files $uri $uri/ =404;
  }

  location /static/ {
    alias /usr/share/nginx/html/assets/;
  }

  location /assets/ {
    expires 7d;
  }
}
```

```sh
curl localhost:8080/                       # index.html
curl localhost:8080/docs/readme.txt        # root + uri
curl localhost:8080/static/site.css        # alias: /static/ replaced by .../assets/
curl -I localhost:8080/assets/site.css     # look at Expires / Cache-Control
curl -I localhost:8080/nope                # 404
```

Now make it behave like a single-page app (client-side routing): change the `/` location to `try_files $uri $uri/ /index.html;` and confirm `curl localhost:8080/some/client/route` returns the home page.

**Takeaways:**
- `root`: file path = root + full URI.
- `alias`: file path = alias + URI *with the location prefix removed*. Keep trailing slashes consistent on both.
- `try_files` checks files in order and falls back to the last argument (a URI or `=code`).

---

## Lesson 5: Reverse proxy basics

**Goal:** forward requests to a backend and understand exactly what path the backend receives.

Start a backend. `traefik/whoami` echoes back the request it received (method, path, headers), which makes proxy behavior visible:

```sh
docker run -d --name app1 --network nginx-lab -e WHOAMI_NAME=app1 traefik/whoami
```

Containers on the same Docker network reach each other by name, so `app1` is a valid hostname for nginx.

```nginx
server {
  listen 80;

  location /a/ { proxy_pass http://app1; }
  location /b/ { proxy_pass http://app1/; }
  location /c/ { proxy_pass http://app1/v2/; }
  location /d/ { proxy_pass http://app1/v2; }
}
```

```sh
ngx-reload
for p in a b c d; do curl -s localhost:8080/$p/users | grep -E '^(GET|Name)'; done
```

Look at the `GET` line of each:

| Location | `proxy_pass` | Backend sees |
| --- | --- | --- |
| `/a/` | `http://app1` | `/a/users` |
| `/b/` | `http://app1/` | `/users` |
| `/c/` | `http://app1/v2/` | `/v2/users` |
| `/d/` | `http://app1/v2` | `/v2users` |

Rule: if `proxy_pass` has **no URI part**, the original request URI is passed unchanged. If it **has** one (even just `/`), the part matched by the location is replaced with it.

**Try:** `docker stop app1`, then `ngx-reload`. It fails with `host not found in upstream`. nginx resolves hostnames when it loads config, so backends must exist at startup/reload. `docker start app1` and reload again. (Lesson 7 shows how to avoid this.)

**Takeaways:** the trailing slash in `proxy_pass` changes the path; container names work as hostnames on a shared Docker network; `127.0.0.1` inside the container is the nginx container itself, not your host.

---

## Lesson 6: Proxy headers and the inheritance trap

**Goal:** pass client information to the backend and learn why headers silently disappear.

Using the `/b/` location from lesson 5, look at the headers whoami prints:

```sh
curl -s localhost:8080/b/ | grep -iE '^(Host|X-)'
```

The backend sees `Host: app1` and no client IP. By default nginx sets `Host` to the upstream's name. Most frameworks need the original host, scheme and client IP to build URLs, redirects and logs correctly:

```nginx
server {
  listen 80;

  proxy_set_header Host $host;
  proxy_set_header X-Real-IP $remote_addr;
  proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
  proxy_set_header X-Forwarded-Proto $scheme;

  location /b/ {
    proxy_pass http://app1/;
  }

  location /trap/ {
    proxy_set_header X-Lesson 6;
    proxy_pass http://app1/;
  }
}
```

```sh
ngx-reload
curl -s localhost:8080/b/    | grep -iE '^(Host|X-)'   # all forwarding headers present
curl -s localhost:8080/trap/ | grep -iE '^(Host|X-)'   # only X-Lesson! Host is back to app1
```

`/trap/` set one `proxy_set_header`, so it inherits **none** from the server block. The standard fix is a snippet file included where needed. Create `conf.d/proxy_headers.inc` (not `.conf`, so it isn't auto-loaded at http level):

```nginx
proxy_set_header Host $host;
proxy_set_header X-Real-IP $remote_addr;
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
proxy_set_header X-Forwarded-Proto $scheme;
```

and in each location: `include /etc/nginx/conf.d/proxy_headers.inc;`. Rewrite `/trap/` that way and confirm all headers come through.

The same trap applies to `add_header` (response headers): one `add_header` in a location discards all inherited ones.

**Takeaways:** always forward `Host` and `X-Forwarded-*`; array-like directives don't merge across levels; use `include` snippets.

---

## Lesson 7: Upstreams and load balancing

**Goal:** balance across several backends and see failover.

```sh
docker run -d --name app2 --network nginx-lab -e WHOAMI_NAME=app2 traefik/whoami
```

At the top of `conf.d/default.conf`, outside the `server` block (this is http context):

```nginx
upstream app {
  server app1:80;
  server app2:80;
  # least_conn;            # try these one at a time
  # ip_hash;
}

server {
  listen 80;

  location / {
    include /etc/nginx/conf.d/proxy_headers.inc;
    proxy_pass http://app;
    proxy_connect_timeout 2s;          # default is 60s; fail over to the next server quickly
    add_header X-Upstream $upstream_addr always;
  }
}
```

```sh
ngx-reload
for i in $(seq 6); do curl -s localhost:8080/ | grep Name; done   # alternates app1/app2
docker stop app2
for i in $(seq 6); do curl -si localhost:8080/ | grep -E 'Name|X-Upstream'; done
docker start app2
```

With `app2` stopped, requests still succeed: when connecting to one server fails, nginx retries the next one (`proxy_next_upstream`, default `error timeout`) and marks the failed one unavailable for a while (`max_fails=1 fail_timeout=10s` by default).

**Try:** mark `app2` as `backup` (`server app2:80 backup;`) and confirm it only receives traffic when `app1` is stopped.

**Dynamic backends:** nginx resolves `app1`/`app2` once at load. If a container is recreated with a new IP, nginx keeps the old one. With a variable in `proxy_pass` and a `resolver`, nginx resolves at request time and also starts fine when the backend is down:

```nginx
location /dyn/ {
  resolver 127.0.0.11 valid=10s;      # Docker's embedded DNS
  set $backend app1;
  proxy_pass http://$backend$request_uri;   # with a variable, build the full URI yourself
}
```

**Takeaways:** `upstream` = a pool with round-robin by default; passive health checks via retries; `$upstream_addr` shows who answered; use `resolver` + variables for backends that move.

---

## Lesson 8: Timeouts, body size and buffering

**Goal:** control the limits that cause most production 413/502/504 errors.

whoami supports `?wait=<duration>` to simulate a slow backend.

```nginx
upstream app {
  server app1:80;
}

server {
  listen 80;
  client_max_body_size 1m;           # default is 1m

  location / {
    include /etc/nginx/conf.d/proxy_headers.inc;
    proxy_pass http://app;
    proxy_connect_timeout 2s;
    proxy_read_timeout 3s;
  }
}
```

```sh
ngx-reload
curl -s -o /dev/null -w '%{http_code}\n' "localhost:8080/?wait=1s"   # 200
curl -s -o /dev/null -w '%{http_code}\n' "localhost:8080/?wait=5s"   # 504 after ~3s

head -c 2M /dev/urandom > big.bin
curl -s -o /dev/null -w '%{http_code}\n' -F file=@big.bin localhost:8080/   # 413
```

Raise `client_max_body_size` to `5m`, reload, and confirm the upload passes.

**Buffering:** by default nginx reads the whole request body before sending it upstream, and buffers responses (spilling to disk if large). That protects slow backends from slow clients, but hurts large uploads/downloads and breaks streaming (Server-Sent Events, long polling). For those locations:

```nginx
proxy_request_buffering off;   # stream uploads to the backend
proxy_buffering off;           # stream responses to the client
```

**Takeaways:** 413 = `client_max_body_size`; 504 = `proxy_read_timeout`; 502 = backend refused/unreachable; turn buffering off for streaming endpoints.

---

## Lesson 9: Logging and debugging

**Goal:** get logs that answer "what happened to this request?"

The official image links logs to stdout/stderr, so they are in `docker logs`. Add a custom format at http level (top of the file):

```nginx
log_format timed '$remote_addr "$request" $status $body_bytes_sent '
                 'upstream=$upstream_addr rt=$request_time urt=$upstream_response_time';
```

and in the server block:

```nginx
access_log /var/log/nginx/access.log timed;
error_log  /var/log/nginx/error.log info;
```

```sh
ngx-reload
ngx-logs &               # follow logs in the background
curl -s "localhost:8080/?wait=1s" > /dev/null
kill %1
```

`rt` (total time) much larger than `urt` (backend time) means the time went to the client side (slow network or big response); `urt` close to `rt` means the backend is slow.

**Debugging toolkit:**

```sh
docker exec web nginx -T             # the full effective config, all includes expanded
curl -v localhost:8080/path          # request/response headers
curl --path-as-is localhost:8080/a/../b   # send URIs without curl normalizing them
```

A temporary `add_header X-Debug-Uri $uri always;` or `return 200 "$uri $args\n";` is often the fastest way to see what nginx is doing in a location.

**Takeaways:** custom `log_format` with upstream timings; `nginx -T` to see what is actually loaded; `return` is your `printf`.

---

## Lesson 10: Redirects and rewrites

**Goal:** know when to use `return` and when `rewrite`.

```nginx
server {
  listen 80;
  default_type text/plain;

  location = /old-page   { return 301 /new-page; }
  location = /new-page   { return 200 "new page\n"; }

  location /blog/ {
    rewrite ^/blog/(\d+)$ /posts?id=$1 last;   # internal: re-runs location matching
  }

  location = /posts      { return 200 "post id=$arg_id\n"; }

  location /go/ {
    rewrite ^/go/(.*)$ https://example.com/$1 redirect;   # external 302
  }
}
```

```sh
ngx-reload
curl -i localhost:8080/old-page      # 301 + Location
curl -L localhost:8080/old-page      # follows it
curl localhost:8080/blog/42          # internal rewrite, no redirect visible to the client
curl -i localhost:8080/go/docs       # 302 to example.com
```

**Takeaways:** `return` for simple redirects (fast, clear); `rewrite ... last` for internal URL changes that need regex captures; `redirect`/`permanent` flags send 302/301 to the client. Avoid `if` for anything except `return`/`rewrite`: it has surprising semantics in `location` blocks.

---

## Lesson 11: Configuration from environment variables

**Goal:** reuse one config across environments.

nginx config has no env var support, but the official image runs `envsubst` on `/etc/nginx/templates/*.template` at startup and writes the result to `/etc/nginx/conf.d/`. This needs a container restart (not a reload) because templates are rendered only at start.

`templates/default.conf.template`:

```nginx
server {
  listen 80;
  location / {
    include /etc/nginx/conf.d/proxy_headers.inc;
    proxy_pass http://${BACKEND_HOST}:${BACKEND_PORT};
  }
}
```

Since the image writes into `conf.d/`, that directory can't be a read-only mount for this lesson. Run a separate container:

```sh
cp conf.d/proxy_headers.inc templates/proxy_headers.inc.template
docker run -d --name web-env --network nginx-lab -p 8081:80 \
  -e BACKEND_HOST=app2 -e BACKEND_PORT=80 \
  -v "$PWD/templates:/etc/nginx/templates:ro" \
  nginx:alpine

curl -s localhost:8081 | grep Name                         # app2
docker exec web-env cat /etc/nginx/conf.d/default.conf     # see the rendered config
docker rm -f web-env
```

**Takeaways:** templates are rendered once at start; only variables that exist in the environment are replaced, so `$host` and friends are safe.

---

## Lesson 12: HTTPS

**Goal:** terminate TLS in nginx and redirect HTTP to HTTPS.

```sh
openssl req -x509 -newkey rsa:2048 -nodes -days 365 \
  -keyout certs/lab.key -out certs/lab.crt -subj "/CN=localhost"
```

```nginx
server {
  listen 80;
  return 301 https://$host:8443$request_uri;   # :8443 only because of the lab's port mapping
}

server {
  listen 443 ssl;
  http2 on;
  ssl_certificate     /etc/nginx/certs/lab.crt;
  ssl_certificate_key /etc/nginx/certs/lab.key;
  ssl_protocols TLSv1.2 TLSv1.3;

  add_header Strict-Transport-Security "max-age=300" always;

  location / {
    include /etc/nginx/conf.d/proxy_headers.inc;
    proxy_pass http://app;
  }
}
```

(Keep the `upstream app` block from earlier at the top of the file.)

```sh
ngx-reload
curl -i localhost:8080/                         # 301 to https
curl -sk https://localhost:8443/ | grep -iE 'Name|X-Forwarded-Proto'   # proto = https
curl -vk https://localhost:8443/ 2>&1 | grep -E 'SSL connection|ALPN'
```

The backend still speaks plain HTTP; `X-Forwarded-Proto: https` is how it learns the client used TLS. In production use real certificates (e.g. Let's Encrypt) and a long HSTS `max-age`.

**Takeaways:** nginx as TLS terminator; HTTP→HTTPS with `return 301`; forward the scheme to the backend.

---

## Lesson 13: Protecting a backend

**Goal:** expose only part of a backend and limit abuse.

Expose only `/api/` of the backend, block everything else, and rate-limit. At http level (alongside the `upstream app` block from lesson 7):

```nginx
limit_req_zone $binary_remote_addr zone=perip:10m rate=5r/s;
```

```nginx
server {
  listen 80;
  server_tokens off;
  client_max_body_size 5m;

  location = /api { proxy_pass http://app/api; include /etc/nginx/conf.d/proxy_headers.inc; }

  location /api/ {
    limit_req zone=perip burst=10 nodelay;
    limit_req_status 429;
    include /etc/nginx/conf.d/proxy_headers.inc;
    proxy_pass http://app/api/;
  }

  location /internal/ {
    allow 127.0.0.1;       # only requests from inside the nginx container
    deny all;
    proxy_pass http://app/internal/;
  }

  location / { return 404; }
}
```

```sh
ngx-reload
curl -s -o /dev/null -w '%{http_code}\n' localhost:8080/api/users           # 200
curl -s -o /dev/null -w '%{http_code}\n' localhost:8080/admin               # 404
curl -s -o /dev/null -w '%{http_code}\n' --path-as-is localhost:8080/api/../admin   # 404
for i in $(seq 30); do curl -s -o /dev/null -w '%{http_code} ' localhost:8080/api/; done; echo   # some 429s

curl -s -o /dev/null -w '%{http_code}\n' localhost:8080/internal/            # 403: comes from the Docker gateway IP
docker exec web wget -qO- http://127.0.0.1/internal/ | grep GET              # allowed from inside
```

Through a published port, `$remote_addr` is the Docker bridge gateway (e.g. `172.17.0.1`), not your machine's real IP. Behind another proxy or load balancer it would be that proxy's IP; the `realip` module (`set_real_ip_from`, `real_ip_header`) restores the client IP from a trusted proxy's header.

The `/api/../admin` request is blocked because nginx normalizes the URI to `/admin` before matching, and because `proxy_pass` has a URI part, the backend receives the normalized path too, never the raw `..`.

**Try:** change `/api/`'s `proxy_pass` to `http://app` (no URI). It still works for normal paths, but now the backend receives the client's raw URI. Prefer the URI form when the proxy is a security boundary.

**Takeaways:** default-deny with a catch-all `return 404`; rate limit per IP with `limit_req`; IP allow lists; `server_tokens off`.

---

## Lesson 14: Putting it together with Docker Compose

**Goal:** a realistic setup: static frontend + load-balanced API + HTTPS, defined in one file.

`compose.yaml`:

```yaml
services:
  web:
    image: nginx:alpine
    ports: ["8080:80", "8443:443"]
    volumes:
      - ./conf.d:/etc/nginx/conf.d:ro
      - ./html:/usr/share/nginx/html:ro
      - ./certs:/etc/nginx/certs:ro
    depends_on: [app1, app2]
  app1:
    image: traefik/whoami
    environment: [WHOAMI_NAME=app1]
  app2:
    image: traefik/whoami
    environment: [WHOAMI_NAME=app2]
```

**Exercise:** without looking back, write `conf.d/default.conf` so that:

1. HTTP redirects to HTTPS.
2. `/` serves `html/` as a single-page app.
3. `/api/` is load-balanced across `app1` and `app2`, with the `/api` prefix stripped, forwarding headers set, and a 10 MB upload limit.
4. Static assets under `/assets/` are cached for 30 days.
5. Access logs include upstream address and timings.
6. Everything else returns 404 (beyond what the SPA serves).

```sh
docker rm -f web app1 app2       # free the lab ports and names
docker compose up -d
docker compose exec web nginx -t
docker compose exec web nginx -s reload
docker compose logs -f web
```

In Compose, services are reachable by service name (`app1`, `app2`) on the project's default network.

---

## Clean up

```sh
docker compose down 2>/dev/null
docker rm -f web web-env app1 app2 2>/dev/null
docker network rm nginx-lab
rm -rf ~/nginx-lab
```

---

## Where to go next

- **Official docs:** [nginx.org/en/docs](https://nginx.org/en/docs/). The [directive index](https://nginx.org/en/docs/dirindex.html) lists the context, default and syntax of every directive; get used to checking "Context:" and "Default:".
- **How requests are processed:** [nginx.org/en/docs/http/request_processing.html](https://nginx.org/en/docs/http/request_processing.html).
- **Pitfalls:** [Pitfalls and Common Mistakes](https://www.nginx.com/resources/wiki/start/topics/tutorials/config_pitfalls/) and "If is Evil".
- **Topics not covered here:** caching (`proxy_cache`), gzip/brotli, the `stream` module (TCP/UDP proxying), `auth_request` (delegating auth to a service), `map` for clean conditional logic, `split_clients` for A/B tests, tuning `worker_connections` and keepalive.
- **Config tooling:** [gixy](https://github.com/dvershinin/gixy) finds common security mistakes in nginx configs.
