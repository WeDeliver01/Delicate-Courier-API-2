"""Build a credential-redacted source export without modifying original files."""
import json
import pathlib
import re
import subprocess
import tempfile
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "exports" / "delicate-couriers-source-backup.zip"
EXCLUDE = {".git", ".agents", ".local", ".cache", ".config", ".pythonlibs",
           "node_modules", ".next", "bin", "obj", "publish", "dist", "exports",
           "TestResults", ".vercel"}
SECRET_NAMES = """ADMIN_EMAIL ADMIN_PASSWORD GOOGLE_MAPS_API_KEY Jwt__SecretKey
NEXT_PUBLIC_SUPABASE_ANON_KEY NEXT_PUBLIC_SUPABASE_URL RATES_DEBUG_SECRET
RESEND_API_KEY SESSION_SECRET SHIPLOGIC_BEARER_TOKEN SHIPLOGIC_TOKEN_TENANT_2_NEW
SUPABASE_CONNECTION_STRING SUPABASE_JWT_SECRET SUPABASE_SERVICE_ROLE_KEY""".split()
PATTERNS = [
    r"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
    r"\b(?:ck|cs)_[a-fA-F0-9]{20,}\b",
    r"\bshp(?:at|ss|ca|pa)_[A-Za-z0-9]+\b",
    r"\bAIza[A-Za-z0-9_-]{20,}\b",
    r"\b(?:sk_live_|sk_test_|re_)[A-Za-z0-9_-]{20,}\b",
    r"\b(?:postgres(?:ql)?|https?)://[^/\s:@]+:[^@\s/]+@[^'\")\s]+",
    r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
]


def sanitize(text):
    for pattern in PATTERNS:
        text = re.sub(pattern, "[REDACTED]", text)
    # Literal credentials in configuration, sample scripts, and documentation.
    text = re.sub(
        r"""(?im)((?:["']?)[\w:.-]*(?:password|secret|token|apikey|api_key|consumerkey|consumersecret|connectionstring)[\w:.-]*(?:["']?)\s*[:=]\s*)(["'])([^"'\r\n]+)\2""",
        lambda m: m.group(1) + m.group(2) + "[REDACTED]" + m.group(2)
        if not any(x in m.group(3) for x in ("${", "process.env", "Environment.", "config["))
        else m.group(0),
        text,
    )
    text = re.sub(r"(?i)(Password\s*=\s*)[^;\"'\r\n]+", r"\1[REDACTED]", text)
    return text


def main():
    files = subprocess.check_output(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=ROOT
    ).decode().split("\0")
    # Include uploaded images/documents even if ignored, but not opaque archives.
    assets = ROOT / "attached_assets"
    if assets.exists():
        files += [str(p.relative_to(ROOT)) for p in assets.rglob("*") if p.is_file()]
    skipped, redacted, included = [], [], []
    OUT.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as archive:
        for name in sorted(set(files)):
            p = pathlib.Path(name)
            if not name or any(part in EXCLUDE for part in p.parts):
                continue
            if (p.name.startswith(".env") or p.suffix.lower() in
                    {".zip", ".gz", ".7z", ".tar", ".bak", ".dump", ".db", ".sqlite",
                     ".log", ".pem", ".pfx", ".p12", ".key", ".dll", ".pdb"}
                    or "credentials" in p.name.lower()):
                skipped.append(name)
                continue
            src = ROOT / p
            if not src.is_file() or src.is_symlink():
                continue
            raw = src.read_bytes()
            try:
                text = raw.decode("utf-8")
            except UnicodeDecodeError:
                # Only expected media/document binaries; no executables or backups.
                if p.suffix.lower() not in {".png", ".jpg", ".jpeg", ".gif", ".webp",
                                            ".ico", ".woff", ".woff2", ".ttf", ".pdf",
                                            ".docx", ".xlsx"}:
                    skipped.append(name)
                    continue
                archive.writestr(name, raw)
            else:
                cleaned = sanitize(text)
                if cleaned != text:
                    redacted.append(name)
                archive.writestr(name, cleaned)
            included.append(name)

        # Read-only schema dump via shell expansion: credentials are never printed.
        with tempfile.TemporaryDirectory() as temp:
            schema = pathlib.Path(temp) / "schema.sql"
            result = subprocess.run(
                'pg_dump "$SUPABASE_CONNECTION_STRING" --schema-only --schema=public '
                '--no-owner --no-privileges',
                shell=True, stdout=schema.open("wb"), stderr=subprocess.PIPE, cwd=ROOT
            )
            schema_ok = result.returncode == 0
            if schema_ok:
                archive.writestr("backup/database-public-schema.sql", sanitize(schema.read_text()))
            else:
                archive.writestr("backup/DATABASE-EXPORT-NOT-INCLUDED.txt",
                                 "Schema export failed. Existing EF migrations remain in the source tree.\n")
                inventory = subprocess.run(
                    """psql "$SUPABASE_CONNECTION_STRING" -X -Atc "SELECT json_agg(c) FROM (SELECT table_schema,table_name,column_name,ordinal_position,data_type,is_nullable,character_maximum_length FROM information_schema.columns WHERE table_schema='public' ORDER BY table_name,ordinal_position) c;" """,
                    shell=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=ROOT
                )
                if inventory.returncode == 0:
                    columns = json.loads(inventory.stdout)
                    archive.writestr("backup/database-column-inventory.json", json.dumps(columns, indent=2))
        archive.writestr("backup/secrets.example.env",
                         "# Names only. Transfer real values separately using secure secret management.\n"
                         + "".join(f"{name}=\n" for name in SECRET_NAMES))
        archive.writestr("backup/README.md", f"""# Delicate Couriers source backup

Contains the current working-tree backend, frontend, WooCommerce plugins,
project documentation, dependency lockfiles, migrations, and selected uploaded assets.
Public database schema included: {schema_ok}.

## Important limitations
This is NOT a full disaster-recovery backup. No live database rows, authentication
accounts, Hangfire jobs, external storage objects, or secret values are included.
Obtain a separate encrypted backup from the existing Supabase database and transfer
it privately, along with external storage and secrets. Never commit those backups.
Git history, dependencies, generated builds, caches, logs, local agent state, nested
archives, .env files and private-key files are excluded. Credential-like literals
in text files are redacted; see manifest.json. Review uploaded documents before sharing.

## Running the source
Install the SDK/runtime versions specified by project files. Restore .NET packages
in DelicateCouriers and run npm ci in delicate-couriers-frontend.
Restore the database into a separate environment, never over the live database.
Supply the required secrets listed in secrets.example.env through secure configuration.
Replace redacted configuration placeholders with your own settings.
Backend: cd DelicateCouriers/DelicateCouriers.ApiService && dotnet run --no-launch-profile --urls http://0.0.0.0:8080
Frontend: cd delicate-couriers-frontend && npm run dev -- -p 5000 -H 0.0.0.0
Consult the root README.md, replit.md, Documentation/, .replit and project configuration
for setup details. Source is exported as-is; this archive does not certify a fresh restore.
""")
        archive.writestr("backup/manifest.json", json.dumps(
            {"included": included, "excluded": skipped, "redacted": redacted,
             "live_database_rows_included": False, "public_schema_included": schema_ok}, indent=2))
    with zipfile.ZipFile(OUT) as archive:
        assert archive.testzip() is None, "ZIP integrity check failed"
        print(json.dumps({"archive": str(OUT.relative_to(ROOT)), "files": len(archive.namelist()),
                          "bytes": OUT.stat().st_size, "schema_included": schema_ok,
                          "redacted_files": len(redacted)}))


if __name__ == "__main__":
    main()