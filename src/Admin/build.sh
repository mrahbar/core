#!/usr/bin/env bash
set -e

DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"

echo -e "\n## Building Admin"

echo -e "\nBuilding app"
echo ".NET Core version $(dotnet --version)"
echo "Restore"
dotnet restore $DIR/Admin.csproj
echo "Clean"
dotnet clean $DIR/Admin.csproj -c "Release" -o $DIR/obj/Docker/publish
echo "Node Build"
npm --prefix $DIR install $DIR
gulp --gulpfile $DIR/gulpfile.js build
echo "Publish"
dotnet publish $DIR/Admin.csproj -c "Release" -o $DIR/obj/Docker/publish

echo -e "\nBuilding docker image"
docker --version
docker build -t bitwarden/admin $DIR/.
