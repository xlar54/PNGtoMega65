# PNGtoMega65

A C# desktop app that converts PNG files to IFF files which can be loaded and displayed on the
MEGA65 computer via BASIC:

10 SCREEN 320,200,8

20 LOADIFF"file.iff"

30 GETKEY A$

40 SCREEN CLOSE

Under the hood, it executes a python script so make sure python3 is installed, and "pip pillow" is run