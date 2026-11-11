proxy_set_header Host $host;
proxy_set_header X-Real-IP $remote_addr;
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
proxy_set_header X-Forwarded-Proto $scheme;

# Stream uploads and downloads straight through instead of buffering them to disk.
proxy_request_buffering off;
proxy_buffering off;
