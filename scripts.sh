#!/bin/bash

################################################## variables ##################################################
azurite_image="mcr.microsoft.com/azure-storage/azurite:latest"
azurite_container="azurite"
nginx_image="nginx:alpine"
nginx_container="nginx"

################################################## functions ##################################################

docker_install_azurite(){
  # sudo snap install storage-explorer
  docker container rm $azurite_container --force
  # --skipApiVersionCheck: Azure SDKs can be ahead of the latest Azurite release
  docker run --name $azurite_container --detach -p 10000:10000 -p 10001:10001 -p 10002:10002 $azurite_image \
    azurite --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0 --skipApiVersionCheck
}

# Proxies http://localhost:5105/demo... to the Document API (dotnet run --project Document).
# --network host because Kestrel listens on localhost:5104 only, which a bridged container can't reach.
docker_install_nginx(){
  docker container rm $nginx_container --force
  docker run --name $nginx_container --detach --network host \
    -v "$PWD/nginx/default.conf:/etc/nginx/conf.d/default.conf:ro" \
    -v "$PWD/nginx/proxy_headers.inc:/etc/nginx/conf.d/proxy_headers.inc:ro" \
    $nginx_image
}

solution_initilize(){
  dotnet tool restore
}

generate_rsa_keys() {
  output_dir="${1:-.}/Keys"
  mkdir -p "${1:-.}/Keys"
  openssl genrsa -out "$output_dir/private.key" 2048
  openssl rsa -in "$output_dir/private.key" -pubout -out "$output_dir/public.key"
}

update_dotnet_packages() {
  find . -name "*.csproj" | sort | while read csproj; do
    echo "Processing project: $csproj"
    dotnet list "$csproj" package --outdated | grep -E '>' | awk '{print $2, $4}' |
    while read name version; do
      echo "Updating package: $name to project: $csproj"
      dotnet add "$csproj" package "$name"
    done
  done
  # dotnet list package --outdated
  dotnet tool update --all # Update all tools
}

run_test() {
  project=$1
  rm -rf $project/TestResults
  dotnet test $project --settings $project/coverlet.runsettings --collect:"XPlat Code Coverage"
  dotnet reportgenerator -reports:"$project/TestResults/*/coverage.cobertura.xml" -targetdir:"$project/TestResults/CoverageReport" -reporttypes:Html
  # echo "$PWD/$project/TestResults/CoverageReport/index.html"
  firefox "$PWD/$project/TestResults/CoverageReport/index.html" &
  # google-chrome "$PWD/$project/TestResults/CoverageReport/index.html" &
  # /opt/microsoft/msedge/msedge "$PWD/$project/TestResults/CoverageReport/index.html" &
}

################################################## execute ##################################################
clear
# docker_install_azurite
docker_install_nginx
# solution_initilize
# generate_rsa_keys Document
# update_dotnet_packages

# run_test "Document.Tests"
