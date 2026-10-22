@echo off
rem Personal launcher: borderless fullscreen, then the normal GUI launcher.
rem Upscaler/output settings live in bbport.ini; keep the launcher's FSR box unticked (FSR corrupts geometry on the Arc iGPU).
set "BB_FULLSCREEN=1"
rem Debug: optimisation-disable mask, re-read every 250 ms (bits in gpu/shim/bbport_toggles.h).
set "BB_TOGGLE_FILE=%~dp0bb_toggles.txt"
rem Measuring session: FPS and thread statistics in launcher.log every few seconds.
set "BB_FRAME_STATS=1"
rem Upscaler frame dumps: creating dump.trigger saves the next 8 frames' upscaler images to out\dump.
set "BB_DUMP_TRIGGER=%~dp0dump.trigger"
set "BB_DUMP_DIR=%~dp0out\dump"
call "%~dp0launch_gui.bat"
