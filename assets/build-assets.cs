// The entry point bin/build-assets runs as a .NET file-based app; the work is in
// src/Campfire.Web/Assets/BuildAssetsCommand.cs.
#:project ../src/Campfire.Web/Campfire.Web.csproj

return Campfire.Web.Assets.BuildAssetsCommand.Run(args, Console.Out);
