# DCS Lua Connector
This repository contains:
## DCS.Lua.Connector
A small DotNet 5.0 library to send commands directly to the DCS lua environment

## DCS.Lua.InteractiveConsole
A small console application that provides a front end for DCS.Lua.Connector

## DCS-LuaConnector-hook.lua
A DCS hook that uses UDP to talk to DCS.Lua.Connector and execute the commands in the DCS environment, parts of this were forked from the lua console in https://github.com/dcs-bios/dcs-bios

Install by placing in `/Saved Games/DCS/Scripts/Hooks`
