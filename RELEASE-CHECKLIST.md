# Release checklist

## Licensing decision — required before distribution

The project bundles XbrzSharp (GPL-3.0-only) and DirectShowLib.Standard (LGPL-2.1).
Single-file packaging does not remove their obligations. The repository being
private is not itself a distribution policy. No license for the application's own
source has been selected automatically by this work.

Before distributing binaries to recipients:

- Choose an application licensing/distribution approach compatible with the
  included dependencies, with legal advice if needed.
- Provide the applicable corresponding source and build/relinking materials to
  recipients as required by those licenses, including dependency source versions.
- Include both license texts and `THIRD-PARTY-NOTICES.md` in the release package.
- Verify that the documented unbundled build (`-p:PublishSingleFile=false`) can be
  rebuilt with a modified DirectShow library.
- If those obligations do not fit the intended distribution, replace/remove the
  incompatible dependency before release. Merely switching xBRZ off in the UI does
  not remove the GPL library from the executable.

## Engineering verification

1. Run the default regression suite on a clean Windows checkout. The synthetic
   numerical fixture must be present and run without camera hardware.
2. For a hardware release, run live smoke tests and verify measurements against a
   suitable temperature reference. Check physical USB removal/reinsertion separately.
3. Publish to an empty directory. Check that only the intended executable and
   license/notice files are distributed, not development outputs or captured images.
4. Exercise the single-file executable on a machine with the x64 .NET 10 Desktop
   Runtime. The application is framework-dependent, not NativeAOT.
5. Verify measurement accuracy against an appropriate independent temperature
   reference before making accuracy claims. Software tests are not certification.

Diagnostics and capture files may contain device identity or thermal imagery;
inspect them before attaching them to reports or sharing them.
