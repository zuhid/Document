#!/bin/bash
# Uploads invoice-sample.pdf to the Document API's "demo" project through the nginx proxy.
#
# Usage:
#   ./upload.sh                    # flat upload:   POST /demo
#   ./upload.sh invoices/2026      # folder upload: POST /demo/invoices/2026
#   BASE_URL=http://localhost:5104/demo ./upload.sh   # call the API directly, bypassing nginx

################################################## variables ##################################################
base_url="${BASE_URL:-http://localhost:5105/demo}"
folder="$1"
file="$(dirname "$0")/invoice-sample.pdf" # next to this script, wherever it is run from

################################################## execute ##################################################
url="${base_url%/}${folder:+/$folder}"
echo "POST $url"
curl --silent --show-error --fail-with-body -F "file=@$file" "$url"
echo
