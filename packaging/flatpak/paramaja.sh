#!/bin/sh
# The published app is a folder (the exe, its .NET runtime and native libraries);
# this puts it on PATH under the name the desktop file and the terminal use.
exec /app/lib/paramaja/Paramaja "$@"
