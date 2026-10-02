# Publishing a release to N.I.N.A.'s plugin list

N.I.N.A. shows plugins from the [manifest repository](https://github.com/isbeorn/nina.plugin.manifests). A release is published in two parts: a GitHub release of this repository (built by `.github/workflows/release.yml`), and a pull request that adds its `manifest.json` to the manifest repository.

## One-time setup

1. Make this repository **public**. The manifest repository accepts only open-source plugins, and N.I.N.A. downloads the plugin from the release link, which must be public. The logo and screenshot links in the manifest also only work when it is public.
2. Settings › Actions › General › Workflow permissions: **Read and write permissions**, so the workflow can create releases.
3. Fork [isbeorn/nina.plugin.manifests](https://github.com/isbeorn/nina.plugin.manifests).

## Each release

1. Update `CHANGELOG.md` and the `<Version>` in `src/NINA.ObservatoryPlanner/NINA.ObservatoryPlanner.csproj`.
2. Run the tests: `dotnet test NINA.ObservatoryPlanner.slnx`, and the simulator tests in `tools/e2e` (see README).
3. Commit, then tag and push the tag. The tag is the version, with four numbers and no "v":
   ```powershell
   git tag 1.0.0.0
   git push origin 1.0.0.0
   ```
4. The workflow runs the unit tests, builds the plugin and creates the release with `NINA.ObservatoryPlanner.<version>.zip` and `NINA.ObservatoryPlanner.<version>.manifest.json`. Do not replace the zip afterwards: the manifest holds its checksum.
5. In your fork of the manifest repository, put the manifest at
   `manifests/o/ObservatoryPlanner/3.2.0/manifest.json` (replace the file for each new version).
6. Validate it in the fork: `npm install`, then `node gather.js`. It must report the manifest as valid.
7. Open a pull request to `isbeorn/nina.plugin.manifests` (text below) and answer the reviewers' questions.

To try a version with a few users first, add `"Channel": "Beta"` to the manifest. Only users who add `https://nighttime-imaging.eu/wp-json/nina/v1/beta` under Options › General › Plugin Repositories see it.

## Pull request text

The manifest repository requires disclosing material AI use and an accountable human maintainer. Submit only after you have reviewed and tested the code yourself and can explain and maintain it.

> **Add Observatory Planner 1.0.0.0**
>
> Observatory Planner runs an observatory unattended, SGP style: a target list with start/end constraints and exposure rows, and four stages (Begin, Start of target, Triggers, End) that are plain Advanced Sequencer instruction sets. With a safety monitor it waits for safe, images the targets and shuts down when unsafe, then resumes.
>
> - Source: https://github.com/nikunj3685/nina-observatory-planner (MPL-2.0)
> - Minimum N.I.N.A. version: 3.2.0.9001
> - Installer: GitHub release zip, SHA256 checksum in the manifest
>
> **AI-assisted development:** this plugin was developed with substantial help from an AI coding assistant (Anthropic Claude), which wrote much of the code and the tests under my direction. I am the maintainer. I have reviewed and tested the code, including unit tests and end-to-end runs in N.I.N.A. 3.2 with the ASCOM OmniSim simulators, and I can explain, debug and maintain it.
