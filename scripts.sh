#!/bin/bash

################################################## variables ##################################################
mailhog_image="mailhog/mailhog:v1.0.1"
mailhog_container="mailhog"

################################################## functions ##################################################

docker_install_mailhog(){
  # open in http://localhost:8025
  docker container rm $mailhog_container --force
  docker run --name $mailhog_container --detach --publish 1025:1025 --publish 8025:8025 $mailhog_image
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
# docker_install_mailhog
# solution_initilize
# generate_rsa_keys Notification
# update_dotnet_packages

# run_test "Notification.Tests"
