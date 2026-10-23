### Building VHD Studio ###

#### Requirements ####

* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Visual Studio is optional;
  any editor and `dotnet` work.
* [Inno Setup 7.1 or newer](https://jrsoftware.org/isinfo.php), only needed for the installer.
* Windows SDK `signtool.exe`, only needed for code signing.
* The configured `sign-file` command, for the release signing workflow below.


#### Build and test ####

    dotnet build Source\VhdStudio.sln
    dotnet test  Source\VhdStudio.sln

The default tests include disposable VHDX files and read-only disk-layout queries. The elevated
data-safety suite also attaches and formats its own disposable disks; run it as an administrator.


#### Run from source ####

    dotnet run --project Source\VhdAttach

Attaching goes through the Windows service. To test the service without installing it, run
`VhdStudioService.exe /Interactive` from an elevated prompt. Maintenance and Disk Manager
changes ask for elevation themselves. VSUX attach and detach-and-lock use an elevated helper
with an interactive password prompt; they do not require the service or pass passwords on the command line.


#### Installer ####

    Setup\Publish.ps1                                  # test, publish to .\Publish\VhdStudio, build installer
    Setup\Publish.ps1 -SkipInstaller                   # binaries only
    Setup\Publish.ps1 -CertificateThumbprint <sha1>    # also sign executables and setup
    Setup\Publish.ps1 -SignFile                       # sign with the configured sign-file command
    Setup\Publish.ps1 -SignFile -SelfContained        # signed installer with embedded runtime

The installer is written to `Releases\vhdstudio-<version>-setup.exe`. Set the version in
`Source\Directory.Build.props`.

Production staging is `Publish\VhdStudio`; publishing cleans only that directory and preserves
independently published research prototypes. `Publish\VhdStudio\Recovery\VsuRecover.exe` is always
self-contained and single-file, even when the main app uses a shared runtime. Setup includes it
under `{app}\Recovery` and registers the `.vsux` handler and Explorer verbs. Building does not
install the service or change file associations.

To publish only standalone recovery:

    dotnet publish Research\VsuPrototype\Recovery\VsuRecover.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o Publish\VhdStudio\Recovery

The recovery project compiles the production signature validator/writer directly and has no
runtime dependency on the VHD Studio UI, service, password verifier or companion file. See
[docs/VSUX.md](docs/VSUX.md) for VSUX behavior and disposable-image verification.


#### Continuous integration ####

`.github/workflows/build.yml` builds, tests and packages pushes to `master`, pull requests and
manual runs. It uploads both installers as workflow artifacts. It does not create a GitHub release.

For a release, update `Source\Directory.Build.props` and `CHANGELOG.md`, run the checks, and build
both packages with `-SignFile` (the second with `-SelfContained`). Verify the Authenticode signatures
of the application, service, VSUX library, recovery tool and both installers. Commit and push the
release source, then create a matching tag and GitHub release with both signed installers attached.
Use the changelog for the release notes, including VSUX's experimental status and recovery limits.

The workflow also runs `Setup\Test-Installer.ps1` on its disposable Windows runner. It installs
5.1.0, exercises hung and lingering service processes, switches both installer types, checks app
and service runtime loading, and uninstalls. The script refuses to run on a development PC.
For installer-only changes, dispatch the Build workflow with `installerOnly=true` to run these
checks without repeating the product and disk-safety suites.

CI pins the official Inno Setup 7.1.0 compiler and verifies its download hash and signature.
Do not downgrade the installer compiler: Inno can retain a newer installed uninstaller and its
older product script instead of selecting the new service cleanup code. The installer source
rejects compilers older than 7.1. Use `-InnoCompiler <path-to-ISCC.exe>` for a portable compiler.


#### Project layout ####

| Path | Contents |
|---|---|
| `Source/VhdAttach` | `VhdStudio.exe`: the UI, Maintenance window (`MaintenanceForm.cs`) and Disk Manager (`Storage/`) |
| `Source/VhdAttach-Service` | `VhdStudioService.exe`: attach/detach and auto-mount service. Also holds code shared with the UI (`VirtualDiskImage.cs`, `VhdxHeader.cs`, `Branding.cs`) |
| `Source/VhdAttach-Test` | MSTest unit and integration tests |
| `Source/VhdStudio.Vsux` | VSUX signature, companion file, password and native attach/detach engine |
| `Research/VsuPrototype/Recovery` | Independently published `VsuRecover.exe`, sharing the production signature validator/writer |
| `Setup` | Inno Setup script and publish script |
