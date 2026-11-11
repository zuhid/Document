# nginx

A reverse proxy in front of the Document API that exposes **only the `demo` project**.

| Request to `http://localhost:5105` | Result |
| --- | --- |
| `/demo`, `/demo/...` | Forwarded to the Document API on `http://localhost:5104` |
| Anything else (`/`, `/crm`, `/DEMO`, `/demox`, `/demo/../crm`, ...) | `404` from nginx; never reaches the API |

## Files

- `default.conf`: the server block (port 5105, `demo` routes, 404 for everything else, 30 MB upload limit).
- `proxy_headers.inc`: forwarding headers and streaming settings, included by both `demo` locations.

## Prerequisites

- Docker
- Linux. The container uses `--network host` because the API listens on `localhost` only, and Docker Desktop on Mac or Windows handles host networking differently.

## Run

Run all commands from the repository root.

1. **Start Azurite** (blob storage emulator), unless it is already running:

   ```sh
   docker run --name azurite --detach -p 10000:10000 -p 10001:10001 -p 10002:10002 \
     mcr.microsoft.com/azure-storage/azurite:latest \
     azurite --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0 --skipApiVersionCheck
   ```

2. **Start the Document API** (listens on port 5104):

   ```sh
   dotnet run --project Document
   ```

3. **Start nginx** (listens on port 5105). Either uncomment `docker_install_nginx` in the "execute" section of `scripts.sh` and run `./scripts.sh`, or run the command directly:

   ```sh
   docker container rm nginx --force
   docker run --name nginx --detach --network host \
     -v "$PWD/nginx/default.conf:/etc/nginx/conf.d/default.conf:ro" \
     -v "$PWD/nginx/proxy_headers.inc:/etc/nginx/conf.d/proxy_headers.inc:ro" \
     nginx:alpine
   ```

   The config files are mounted from the repo, so the commands must run from the repository root.

## Check that it works

```sh
curl -F "file=@somefile.pdf" http://localhost:5105/demo   # upload: 200 with the document JSON
curl http://localhost:5105/demo                           # list: 200
curl -i http://localhost:5105/crm                         # blocked: 404
```

The other endpoints (view, download, replace, delete, folders) work the same way as on the API itself. See `Document/Controllers/DocumentController.md` and use `http://localhost:5105/demo` as the base URL.

To point the web app at the proxy, start it with `VITE_API_URL=http://localhost:5105 npm run dev` from `Web/`.

## After changing the config

```sh
docker exec nginx nginx -t          # validate the config
docker exec nginx nginx -s reload   # apply it without restarting the container
```

## Logs and stopping

```sh
docker logs -f nginx                # access and error logs
docker container rm nginx --force   # stop and remove the container
```

## Troubleshooting

- **`502 Bad Gateway`**: the Document API is not running on port 5104. Start it (step 2).
- **Container exits right after starting**: check `docker logs nginx`. Usually another process is already using port 5105, for example an older proxy container. Find it with `ss -ltnp | grep 5105` or `docker ps`.
- **`413 Request Entity Too Large`**: the upload is over nginx's 30 MB limit (`client_max_body_size` in `default.conf`). The API rejects bodies over about 28.6 MB anyway.
