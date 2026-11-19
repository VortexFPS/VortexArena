namespace VortexArena.Net;

/// <summary>Attached countdown sprite. A zero digit clears the attachment.</summary>
public readonly record struct CountdownIndicatorState(byte Digit, int TargetTeam);
