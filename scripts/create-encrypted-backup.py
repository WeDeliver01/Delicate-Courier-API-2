"""Export source and PostgreSQL into an age-encrypted archive.

Does not export process environment values. Only the encrypted payload is retained.
"""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import zipfile


def run(command, **kwargs):
    result = subprocess.run(command, stderr=subprocess.PIPE, **kwargs)
    if result.returncode:
        # Do not echo provider errors: these may include connection information.
        raise RuntimeError("Backup command failed; no downloadable backup was produced.")
    return result


def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--recipient", required=True)
    parser.add_argument("--pg-bin", required=True)
    args = parser.parse_args()
    os.umask(0o077)
    root = Path(__file__).resolve().parents[1]
    pg = Path(args.pg_bin)
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    destination = root / "exports" / f"delicate-couriers-encrypted-backup-{stamp}.zip"
    destination.parent.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="courier-backup-") as tmp:
        temp = Path(tmp)
        content = temp / "backup"
        content.mkdir()
        db = content / "database.dump"
        print("Exporting database with a consistent PostgreSQL snapshot.", flush=True)
        with db.open("wb") as f:
            run(f'{pg}/pg_dump "$SUPABASE_CONNECTION_STRING" --format=custom',
                shell=True, stdout=f)
        with (content / "database-toc.txt").open("wb") as f:
            run([str(pg / "pg_restore"), "--list", str(db)], stdout=f)
        # Parse/decompress the whole dump without executing any SQL.
        run([str(pg / "pg_restore"), "--file=/dev/null", str(db)],
            stdout=subprocess.DEVNULL)
        print("Database dump exported and fully read back.", flush=True)
        inventory_sql = """
        SELECT json_build_object(
          'server_version',current_setting('server_version'),
          'extensions',(SELECT json_agg(extname) FROM pg_extension),
          'roles',(SELECT json_agg(rolname) FROM pg_roles),
          'storage_object_count',(SELECT count(*) FROM storage.objects),
          'auth_user_count',(SELECT count(*) FROM auth.users),
          'vault_secret_count',(SELECT count(*) FROM vault.secrets),
          'tables',(SELECT json_agg(x) FROM
             (SELECT schemaname,tablename FROM pg_tables
              WHERE schemaname NOT IN ('pg_catalog','information_schema')
              ORDER BY schemaname,tablename) x));
        """
        sql = temp / "inventory.sql"
        sql.write_text(inventory_sql)
        inventory = run(
                            ["/bin/bash", "-c",
                             'psql "$SUPABASE_CONNECTION_STRING" -X -At -f "$1"',
                             "backup", str(sql)], stdout=subprocess.PIPE)
        info = json.loads(inventory.stdout)
        (content / "database-inventory.json").write_text(json.dumps(info, indent=2))
        if info["storage_object_count"]:
            raise RuntimeError("Storage objects exist: export their bytes before delivering this backup.")
        # Database package releases store their actual ZIP bytes in public tables.
        # Supabase storage.objects is empty, so there are no bucket bytes to export.
        source = content / "workspace.tar.gz"
        excluded = {".local", ".cache", ".config", ".pythonlibs", "node_modules",
                    ".next", "bin", "obj", "TestResults", "exports"}
        excluded_paths = []
        total_files = 0
        print("Archiving source, Git history, configuration, documents and uploads.", flush=True)
        with tarfile.open(source, "w:gz") as archive:
            for parent, dirs, files in os.walk(root):
                rel = Path(parent).relative_to(root)
                keep = []
                for name in dirs:
                    path = rel / name
                    if name in excluded or path == Path(".agents/users"):
                        excluded_paths.append(str(path))
                    elif (root / path).is_symlink():
                        excluded_paths.append(str(path))
                    else:
                        keep.append(name)
                dirs[:] = keep
                for name in files:
                    path = rel / name
                    src = root / path
                    if src.is_symlink():
                        excluded_paths.append(str(path))
                        continue
                    archive.add(src, arcname=str(path), recursive=False)
                    total_files += 1
        with tarfile.open(source, "r:gz") as archive:
            for member in archive:
                if member.isfile():
                    with archive.extractfile(member) as stream:
                        while stream.read(1024 * 1024):
                            pass
        required = """ADMIN_EMAIL ADMIN_PASSWORD GOOGLE_MAPS_API_KEY Jwt__SecretKey
NEXT_PUBLIC_SUPABASE_ANON_KEY NEXT_PUBLIC_SUPABASE_URL RATES_DEBUG_SECRET
RESEND_API_KEY SESSION_SECRET SHIPLOGIC_BEARER_TOKEN SHIPLOGIC_TOKEN_TENANT_2_NEW
SUPABASE_CONNECTION_STRING SUPABASE_JWT_SECRET SUPABASE_SERVICE_ROLE_KEY""".split()
        (content / "REQUIRED-SECRETS.txt").write_text(
            "Values are NOT exported from Replit Secrets. Transfer separately, securely.\n"
            + "\n".join(required) + "\n")
        (content / "RESTORE.md").write_text("""# Restore instructions and coverage

## Coverage
database.dump is a full logical PostgreSQL custom-format database dump, including
all accessible non-system schemas and records (application, auth, Hangfire,
storage metadata and extension data). Plugin release ZIP bytes are in the database.
The dump is a consistent point-in-time snapshot. Files were copied afterward;
this is not a coordinated point-in-time snapshot of every external service.
workspace.tar.gz contains current source, Git history, original on-disk
configuration including .env files if present, uploaded assets and documents.
Generated dependency caches, agent runtime caches, and old exports are excluded.
See manifest.json for exclusions. External merchant WooCommerce/Shopify sites,
Shiplogic accounts and shipments, Google settings, email-provider settings,
DNS, and Replit/Supabase control-plane configuration are not captured by this archive.

## Prerequisites and warnings
Use a fresh, isolated Supabase-compatible PostgreSQL 17 environment. A plain
PostgreSQL server lacks Supabase roles, extensions, auth services, and storage
services. Consult database-inventory.json for roles and extensions required.
Provision required roles/extensions via the destination platform; role passwords,
Supabase project JWT/signing configuration, and provider encryption root keys
are NOT contained in a logical database dump.
Replit runtime secret VALUES are NOT included. Transfer the values listed in
REQUIRED-SECRETS.txt separately through secure secret management. Existing
on-disk .env files are included but may not contain all required settings.
If vault_secret_count is nonzero, encrypted Vault entries additionally require
the original compatible Vault encryption configuration or separate secure migration.
Do not claim restore is complete until these external prerequisites are met.

## Safe database recovery
Never restore over the existing live database for a test. Keep all restored web
servers, schedulers and Hangfire workers OFFLINE until reconciliation is complete.
Restored queued jobs can send duplicate emails, write to stores, or book couriers.
Use pg_restore 17 or newer. Inspect database-toc.txt and the destination Supabase
baseline before executing a restore; managed schemas may already exist and require
the provider's documented migration procedure rather than a blind full restore.
For a prepared EMPTY compatible database, PowerShell:

    pg_restore --exit-on-error --no-owner --no-privileges --dbname="$env:RESTORE_DATABASE_URL" .\\database.dump

This command assumes required roles/extensions/services exist and the target has
no conflicting schema. It intentionally does not drop or replace existing objects.
Review and reinstate needed grants/ownership against the destination provider after
restoring. Do not use --clean against your live database.

## Source recovery
Extract workspace.tar.gz into a new directory:

    mkdir restored-workspace
    tar -xzf workspace.tar.gz -C restored-workspace

Install the SDK/runtime versions required by the project and restore dependencies:
dotnet restore in DelicateCouriers; npm ci in delicate-couriers-frontend.
Configure a NEW database connection and auth endpoints in the isolated copy.
Consult the root README, replit.md, .replit, replit.nix and Documentation.
Preserve production credentials privately; use disabled/test integrations for testing.
Do not start the app until the restored background-job queues are reviewed.

## Verification before cutover
The producer checked archive integrity and read every section of database.dump,
but did NOT execute a full restore into a separate Supabase instance.
Verify row counts, authentication, tenant access, order/shipment history,
downloadable plugin releases, required grants, uploaded assets, and delivery times.
Test integrations without creating real courier bookings. Transfer DNS/deployment
settings only after the isolated restore passes. Retain the original system and
encrypted backup until recovery is confirmed.
""")
        manifest = {
            "created_utc": stamp, "source_file_count": total_files,
            "excluded_paths": excluded_paths,
            "database_format": "PostgreSQL custom",
            "storage_object_count": info["storage_object_count"],
            "runtime_secret_values_included": False,
            "live_restore_test_performed": False,
            "integrity_checks": ["pg_restore --list", "pg_restore full SQL readback",
                                 "all workspace tar members read"],
            "sha256": {p.name: digest(p) for p in content.iterdir() if p.is_file()}
        }
        (content / "manifest.json").write_text(json.dumps(manifest, indent=2))
        plaintext = temp / "backup.tar"
        with tarfile.open(plaintext, "w") as archive:
            for path in content.iterdir():
                archive.add(path, arcname=path.name)
        encrypted = temp / "backup.tar.age"
        run(["age", "-r", args.recipient, "-o", str(encrypted), str(plaintext)],
            stdout=subprocess.DEVNULL)
        checksum = digest(encrypted)
        instructions = """# Open your encrypted backup on Windows

1. Extract this ZIP beside age.exe.
2. Open PowerShell in that folder.
3. Verify the encrypted file:
   Get-FileHash .\\backup.tar.age -Algorithm SHA256
   Compare it to backup.tar.age.sha256.
4. Decrypt using your PRIVATE key (never upload it):
   .\\age.exe -d -i "$env:USERPROFILE\\courier-backup-key.txt" -o backup.tar backup.tar.age
5. Extract:
   mkdir decrypted-backup
   tar -xf backup.tar -C decrypted-backup
6. Read decrypted-backup\\RESTORE.md before running any restore commands.

IMPORTANT: Replit runtime secrets must be transferred separately. This package
contains the full accessible database, not just a schema, but cannot independently
recreate external service accounts or provider control-plane settings. No full
restore rehearsal into another Supabase instance has been performed.
Keep decrypted files private: they contain customer data and stored credentials.
"""
        with zipfile.ZipFile(destination, "w", zipfile.ZIP_STORED) as z:
            z.write(encrypted, "backup.tar.age")
            z.writestr("backup.tar.age.sha256", checksum + "  backup.tar.age\n")
            z.writestr("OPEN-ON-WINDOWS.txt", instructions)
        with zipfile.ZipFile(destination) as z:
            assert z.testzip() is None
        print(json.dumps({"path": str(destination.relative_to(root)),
                          "bytes": destination.stat().st_size,
                          "source_files": total_files,
                          "database_bytes": db.stat().st_size,
                          "storage_objects": info["storage_object_count"],
                          "sha256_encrypted_payload": checksum}), flush=True)
    print("Temporary unencrypted backup files removed.", flush=True)


if __name__ == "__main__":
    main()