"""Example plugin: labels git commands and adds a small API route."""
# To use a plugin, copy it into the "plugins" folder of your SSH Manager home folder and restart the
# server (see "Plugins" in the README). Plugins run inside the server with its rights, so only use
# plugins you trust.


def setup(ctx):
    # ctx.config, ctx.home, ctx.monitor, ctx.activity and ctx.app (FastAPI) are available.

    def label_git(rec):
        # rec: {"kind", "summary", "script", "name", "pid", ...} for a command that just started
        if rec["script"].lstrip().startswith("git "):
            rec["kind"] = "git"

    def git_output(rec):
        # return extra fields for the activity entry when the command ends (or None)
        return None

    ctx.add_decoder(label_git)
    ctx.on_command_finished(git_output)

    @ctx.app.get("/api/plugins/example/hello")
    def hello():
        return {"hello": "from the example plugin"}
