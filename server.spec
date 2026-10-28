# PyInstaller spec for SSHManagerServer.exe (one folder). Build: python -m PyInstaller server.spec
# A console program on purpose: --client-info prints to stdout. It's always started hidden
# (HiddenRun.exe in the logon task, or through WMI over SSH), so no window shows.
a = Analysis(
    ["server.py"],
    pathex=["."],
    datas=[("static", "static"), ("config.example.json", ".")],
    hiddenimports=["uvicorn.logging", "uvicorn.loops.auto", "uvicorn.protocols.http.h11_impl", "uvicorn.lifespan.on"],
    excludes=["tkinter", "test", "unittest", "pydoc_data"],
)
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name="SSHManagerServer", console=True,
          icon="app/SSHManager/app.ico", upx=False)
coll = COLLECT(exe, a.binaries, a.datas, name="SSHManagerServer", upx=False)
