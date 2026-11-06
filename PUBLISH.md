# Publishing looot-dotnet

The package id is `Looot` on nuget.org. The release workflow publishes with NuGet trusted
publishing (OIDC), so no API key is stored in GitHub. Nothing is published yet and no tag is
pushed.

## One-time steps for Walid

1. Create or sign in to a nuget.org account at https://www.nuget.org (a Microsoft account works).
   Note the profile name, it is the username shown in the top right.
2. Claim the id. Request the ID prefix `Looot` at
   https://www.nuget.org/account/prefixreservation (optional, it marks the package as yours).
   The package itself is created by the first push.
3. Add the trusted publishing policy: nuget.org, Account, Trusted Publishing, Add policy.
   Repository owner `loootai`, repository `looot-dotnet`, workflow file `release.yml`,
   package `Looot`. Leave the environment empty. If nuget.org only accepts a policy for an
   existing package, do the first push with the API key from step 5, then add the policy.
4. In GitHub, repo `loootai/looot-dotnet`, Settings, Secrets and variables, Actions, Variables,
   add `NUGET_USER` with the nuget.org profile name from step 1. It is not a secret.
5. If nuget.org does not offer trusted publishing for your account yet, fall back to an API key:
   create one at Account, API keys (scope Push, glob `Looot*`), save it as the repo secret
   `NUGET_API_KEY`, and in `release.yml` delete the "NuGet login (OIDC)" step and use
   `--api-key "${{ secrets.NUGET_API_KEY }}"` in the push step.

## Release

1. Update `Version` in `src/Looot/Looot.csproj` and `LoootClient.Version` in
   `src/Looot/LoootClient.cs`, and add a section to `CHANGELOG.md`.
2. Run `dotnet test tests/Looot.Tests` and `bash scripts/leak-scan.sh .`.
3. Commit, then `git tag v0.1.0 && git push origin main v0.1.0`. The workflow checks that the
   tag, the project version and the code constant match, tests, packs, pushes to nuget.org and
   creates the GitHub release.

A pushed NuGet version cannot be changed. Ship a new patch version instead.
