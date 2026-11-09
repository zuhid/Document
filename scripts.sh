#!/bin/bash

################################################## variables ##################################################
azurite_image="mcr.microsoft.com/azure-storage/azurite:latest"
azurite_container="azurite"

################################################## functions ##################################################

docker_install_azurite(){
  docker container rm $azurite_container --force
  docker run --name $azurite_container --detach -p 10000:10000 -p 10001:10001 -p 10002:10002 $azurite_image
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
# solution_initilize
# generate_rsa_keys Document
update_dotnet_packages

# run_test "Document.Tests"
