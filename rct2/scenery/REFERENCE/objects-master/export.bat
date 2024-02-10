@echo off
pushd tools\objexport
    echo Building objexport
    call nuget restore > nul
    call msbuild /nologo /v:m /p:Configuration=Release "/p:Platform=Any CPU"
popd

set RCT2OBJDIR=C:\Program Files (x86)\Infogrames\RollerCoaster Tycoon 2\ObjData
set OUTDIR=objects
call tools\objexport\bin\Release\objexport "%RCT2OBJDIR%" "%OUTDIR%"
