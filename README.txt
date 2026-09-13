Black Ops III Audio Fix
=======================

THE PROBLEM
Black Ops III scans every enabled audio device when it starts. With a lot of
them (Voicemeeter, VB-Cable, virtual headsets, streaming devices, etc.) the
scan never finishes: you hear the intro music but the screen stays black and
the game locks up.

THE FIX
A proxy "winmm.dll" is placed in the Black Ops III folder. When the game asks
Windows for the audio device list, this DLL hands back only the devices you
picked. Every other Windows call is passed straight through to the real
system winmm.dll untouched.

Nothing on your PC is disabled. Windows, Voicemeeter, Discord and every other
program still see all of your audio devices exactly as before. Only Black Ops
III gets the shortened list.


HOW TO USE
1. Run "BO3 Audio Fix Installer.exe".
2. Check that the Black Ops III folder at the top is correct (it is detected
   automatically; use Browse... if not).
3. Tick the devices the game should see. "Recommended" ticks your real
   hardware plus your Windows default device and unticks the virtual ones.
   Fewer is safer - under 8 is comfortable.
4. Click Install, then launch the game normally through Steam.

To change the device list later, run the installer again - it reads your
current settings back in and pre-ticks them.
To go back to stock, run it and click Uninstall.


Requirements on the target PC:
  - 64-bit Windows
  - .NET Framework 4.x (built into Windows 10 and 11 - nothing to install)

If the person's Steam library sits under Program Files, Windows will refuse
write access to the game folder. The installer detects this and offers to
restart itself as administrator.

The device list is read from whatever PC it runs on, and "Recommended" works
off a general rule (real hardware and the current Windows default device get
ticked, known virtual devices do not), so it is not tied to any one system.


WHAT GETS INSTALLED
Three files are copied into the Black Ops III folder:

  winmm.dll        the filter (extracted from the installer)
  winmm_real.dll   an exact copy of C:\Windows\System32\winmm.dll, which the
                   filter forwards all normal calls to
  bo3_audio.cfg    your chosen device list

Any existing bo3_audio.cfg is backed up first as bo3_audio.cfg.bak-<date>.


TUNING BY HAND
bo3_audio.cfg is plain text and the game reads it at every launch:

  keep=<text>    show devices whose name contains this text (repeatable)
  block=<text>   hide matching devices even if a keep= line matched them
  showall=1      turn filtering off completely
  log=1          write bo3_audio.log listing what was shown and hidden

Device names are compared case-insensitively, and Windows truncates them to
31 characters, so keep the text short. Set log=1 and launch the game once if
you want to see the exact names the game is working with.


AFTER A WINDOWS UPDATE
If a Windows update replaces the system winmm.dll, just run the installer
again - it always copies a fresh winmm_real.dll from the current system.


REBUILDING FROM SOURCE
Everything needed is in the src folder:

  winmm_proxy.cpp        the DLL - build with src\build.bat (Visual Studio)
  exports_gen.h          generated export table (168 forwarders + 12 hooks)
  Bo3AudioFixInstaller.cs the installer GUI
  build_exe.bat          builds the EXE and embeds payload\winmm.dll

build_exe.bat uses the C# compiler that ships with .NET Framework, so it
needs no Visual Studio. build.bat (for the DLL) does need Visual Studio.


NOTE ON MULTIPLAYER
This only changes which audio devices the game can see. It does not touch
game code, memory or files. It is the same proxy-DLL technique used by
ReShade and similar tools.
