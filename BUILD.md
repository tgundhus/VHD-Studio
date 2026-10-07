### Building VHD Studio ###

#### Requirements ####

* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Visual Studio is optional;
  any editor and `dotnet` work.
* [Inno Setup 6](https://jrsoftware.org/isinfo.php), only needed for the installer.
* Windows SDK `signtool.exe`, only needed for code signing.


#### Build and test ####

    dotnet build Source\VhdStudio.sln
    dotnet test  Source\VhdStudio.sln

Some tests create a small temporary VHDX and read the local disk layout (read-only). They need
no administrator rights.


#### Run from source ####

    dotnet run --project Source\VhdAttach

Attaching goes through the Windows service. To test the service without installing it, run
`VhdStudioService.exe /Interactive` from an elevated prompt. Maintenance and Disk Manager
changes ask for elevation themselves.


#### Installer ####

    Setup\Publish.ps1                                  # test, publish to .\Publish, build installer
    Setup\Publish.ps1 -SkipInstaller                   # binaries only
    Setup\Publish.ps1 -CertificateThumbprint <sha1>    # also sign executables and setup

The installer is written to `Releases\vhdstudio-<version>-setup.exe`. Set the version in
`Source\Directory.Build.props`.


#### Continuous integration ####

`.github/workflows/build.yml` builds, tests and packages every push. Pushing a tag such as
`v5.0.0` creates a GitHub release with the installer attached.


#### Project layout ####

| Path | Contents |
|---|---|
| `Source/VhdAttach` | `VhdStudio.exe`: the UI, Maintenance window (`MaintenanceForm.cs`) and Disk Manager (`Storage/`) |
| `Source/VhdAttach-Service` | `VhdStudioService.exe`: attach/detach and auto-mount service. Also holds code shared with the UI (`VirtualDiskImage.cs`, `VhdxHeader.cs`, `Branding.cs`) |
| `Source/VhdAttach-Test` | MSTest unit and integration tests |
| `Setup` | Inno Setup script and publish script |
