# resources

`core/` is the core's publish output, dropped here by CI (`dotnet publish … -o src-tauri/resources/core`)
or by `tools/use-stub.sh` for shell-only testing. It is gitignored. The bundle picks up
`resources/core/**` and the shell runs `resources/core/tessera-core(.exe)` from the app's resource dir.
