VERSION=0.2.1

docker run --rm -v "${PWD}:/local" --network host -u $(id -u ${USER}):$(id -g ${USER})  openapitools/openapi-generator-cli generate \
-i http://localhost:5006/swagger/v1/swagger.json \
-g csharp \
-o /local/out --additional-properties=packageName=Coflnet.Sky.Referral.Client,packageVersion=$VERSION,licenseId=MIT

cd out
sed -i 's/GIT_USER_ID/Coflnet/g' src/Coflnet.Sky.Referral.Client/Coflnet.Sky.Referral.Client.csproj
sed -i 's/GIT_REPO_ID/SkyBase/g' src/Coflnet.Sky.Referral.Client/Coflnet.Sky.Referral.Client.csproj
sed -i 's/>OpenAPI/>Coflnet/g' src/Coflnet.Sky.Referral.Client/Coflnet.Sky.Referral.Client.csproj

dotnet pack
cp src/Coflnet.Sky.Referral.Client/bin/Debug/Coflnet.Sky.Referral.Client.*.nupkg ..
