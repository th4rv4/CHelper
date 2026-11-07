namespace CHelper.Hardware;

/// <summary>
/// BIOS hotkey event codes from the Clevo ACPI bridge, recorded on the EVOL P15 23.
/// Keyboard codes match the Linux clevo driver. Unmapped codes are written to the log.
/// </summary>
public static class BiosEvents
{
    /// <summary>Fn+Esc (the vendor opens Control Center on this).</summary>
    public const int FnEsc = 0x95;

    /// <summary>Fn + numpad "-"</summary>
    public const int KbBrightnessDown = 0x81;

    /// <summary>Fn + numpad "+"</summary>
    public const int KbBrightnessUp = 0x82;

    /// <summary>Fn + numpad "/"</summary>
    public const int KbColor = 0x83;

    /// <summary>Fn + numpad "*"</summary>
    public const int KbToggle = 0x9F;

    /// <summary>Fn + touchpad key: the EC toggles the touchpad itself and reports this single code.</summary>
    public const int TouchpadToggle = 0x5D;

    /// <summary>Fn + camera key: the EC switches the camera and reports the new state.</summary>
    public const int CameraOff = 0xF6;
    public const int CameraOn = 0xF7;
}
