using VhdStudio.Vsux;

if (args is not ["--restore", var path]) {
    Console.WriteLine("VsuRecover --restore <image.vsux-or-vhdx>\nRestores only the eight-byte VHDX signature after structural and exclusive-access checks.\nNo password, recovery file, VHD Studio installation, mounting or header snapshot is used.");
    return args is ["--help"] ? 0 : 2;
}
try {
    SignatureFile.Restore(path);
    Console.WriteLine("The eight-byte VHDX signature was restored and verified. The image is now unlocked.\nThe filename and every other image byte were left unchanged. Rename .vsux to .vhdx to use it as a standard virtual disk.");
    return 0;
} catch (Exception ex) {
    Console.Error.WriteLine("Recovery did not complete: " + ex.Message);
    return 1;
}
