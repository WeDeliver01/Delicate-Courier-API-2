#!/usr/bin/env python3
"""
Seed the first SuperAdmin on the portal (app.delicatecourier.co.za).

Run on api01, as root:

    python3 seed-portal-superadmin.py --email admin@delicatecourier.co.za

Why a script rather than the API: POST /api/admin/users already does exactly
this, correctly, including rolling back the Supabase user if the local insert
fails. But it requires an existing Admin or SuperAdmin to call it, and the first
one has nobody to authorise it. This exists only for that first user; every
one after it should go through the console.

Two systems have to agree, which is the whole difficulty:

  * Supabase Auth, which is the system of record for who may sign in. The
    portal uses its own project, separate from the Engine's.
  * The portal's own "User" table, which holds the role and the tenant. Its
    SupabaseUserId must equal the Supabase id, and both Email and
    SupabaseUserId are unique.

A row without a matching Supabase user is an account that can never sign in. A
Supabase user without a row signs in and is refused, with "No local profile for
Supabase user ..." in the API log. This writes both, or neither.

The password is read from the terminal and never printed, logged or passed as
an argument, so it stays out of shell history and out of the process list.
"""

from __future__ import annotations

import argparse
import getpass
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request

API_ENV = "/etc/delicate/api.env"


def read_env(path: str) -> dict[str, str]:
    """
    Parse the service env file.

    Deliberately not `source`d: at least one value contains a space ("SSL
    Mode=Require" inside the connection string), and a shell reads that as a
    command. Python reading it as data cannot be surprised that way.
    """
    out: dict[str, str] = {}
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            out[key.strip()] = value.strip().strip('"').strip("'")
    return out


def pg_env(connection_string: str) -> dict[str, str]:
    """Turn the ADO.NET connection string the API uses into libpq's variables."""
    parts: dict[str, str] = {}
    for piece in connection_string.split(";"):
        if "=" in piece:
            key, value = piece.split("=", 1)
            parts[key.strip().lower().replace(" ", "")] = value.strip()

    env = dict(os.environ)
    env.update(
        PGHOST=parts.get("host", ""),
        PGPORT=parts.get("port", "5432"),
        PGDATABASE=parts.get("database", "postgres"),
        PGUSER=parts.get("username", parts.get("userid", "")),
        PGPASSWORD=parts.get("password", ""),
        PGSSLMODE=parts.get("sslmode", "require").lower(),
    )
    return env


def psql(env: dict[str, str], sql: str) -> str:
    result = subprocess.run(
        ["psql", "-tAc", sql], env=env, capture_output=True, text=True, timeout=60
    )
    if result.returncode != 0:
        sys.exit(f"psql failed: {result.stderr.strip()[:400]}")
    return result.stdout.strip()


def supabase(env: dict[str, str], method: str, path: str, body: dict | None = None) -> dict:
    url = env["NEXT_PUBLIC_SUPABASE_URL"].rstrip("/") + path
    key = env["SUPABASE_SERVICE_ROLE_KEY"]
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(url, data=data, method=method)
    request.add_header("apikey", key)
    request.add_header("Authorization", f"Bearer {key}")
    request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.loads(response.read() or b"{}")
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode(errors="replace")[:300]
        sys.exit(f"Supabase {method} {path} failed: HTTP {exc.code} {detail}")


def find_supabase_user(env: dict[str, str], email: str) -> str | None:
    """The Supabase id for an address, or None. Paged, because the admin list is."""
    page = 1
    while page <= 20:
        result = supabase(env, "GET", f"/auth/v1/admin/users?page={page}&per_page=200")
        users = result.get("users", [])
        if not users:
            return None
        for user in users:
            if (user.get("email") or "").lower() == email:
                return user.get("id")
        page += 1
    return None


def set_password(env: dict[str, str], email: str) -> None:
    """
    Set the password on an account that already exists.

    This exists because the portal has no forgot-password page. The API delegates
    reset to "Supabase directly from the client", and that page was never built,
    so an account with no password has no way in and an account whose password is
    forgotten has no way back. Until that is built, this is the only route.

    The password is typed here and goes straight to Supabase. It is never printed,
    never passed as an argument and never stored, so it stays out of shell history
    and out of the process list.
    """
    user_id = find_supabase_user(env, email)
    if not user_id:
        sys.exit(f"No Supabase user for {email} in this project.")

    print(f"Setting the password for {email} ({user_id}).")
    password = getpass.getpass("New password (not echoed): ")
    if len(password) < 12:
        sys.exit("Refusing a password under 12 characters for an admin account.")
    if password != getpass.getpass("Repeat it: "):
        sys.exit("They did not match. Nothing was changed.")

    supabase(env, "PUT", f"/auth/v1/admin/users/{user_id}", {"password": password})
    print("Done. Sign in at https://app.delicatecourier.co.za/login")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--email", required=True)
    parser.add_argument("--first-name", default="Platform")
    parser.add_argument("--last-name", default="Admin")
    parser.add_argument(
        "--tenant-id",
        type=int,
        help="Tenant to attach to. Omit to list the available ones and stop.",
    )
    parser.add_argument(
        "--set-password",
        action="store_true",
        help=(
            "Set the password on an account that already exists, and do nothing else. "
            "The portal has no forgot-password page, so an account created without one "
            "cannot otherwise be got into."
        ),
    )
    parser.add_argument(
        "--no-password",
        action="store_true",
        help=(
            "Create the account without setting a password; they set their own through "
            "Forgot password. Use this when whoever runs the script should not be "
            "choosing somebody else's credential."
        ),
    )
    parser.add_argument(
        "--role",
        default="SuperAdmin",
        help="SuperAdmin unless you have a reason; Admin cannot reach the super-admin screens.",
    )
    args = parser.parse_args()

    env = read_env(API_ENV)
    for required in ("SUPABASE_CONNECTION_STRING", "SUPABASE_SERVICE_ROLE_KEY", "NEXT_PUBLIC_SUPABASE_URL"):
        if not env.get(required):
            sys.exit(f"{required} is missing or empty in {API_ENV}")

    db = pg_env(env["SUPABASE_CONNECTION_STRING"])
    email = args.email.strip().lower()

    if args.set_password:
        set_password(env, email)
        return

    # Nothing is created before this. A second row for an address that already
    # has one would fail on the unique index anyway, but failing here says why.
    existing = psql(db, f"""SELECT "UserID" || ' | ' || "Role" FROM "User" WHERE lower("Email") = '{email}'""")
    if existing:
        print(f"A portal user already exists for {email}: {existing}")
        print("Nothing was changed. Change its role directly if that is what you meant.")
        return

    if args.tenant_id is None:
        print("Tenants available:")
        print(psql(db, 'SELECT "TenantID" || \'  \' || "TenantName" FROM "Tenants" ORDER BY "TenantID"') or "  (none)")
        print("\nRe-run with --tenant-id <id>. The column is a required foreign key,")
        print("and the User row is cascade-deleted with its tenant, so it matters.")
        return

    tenant = psql(db, f'SELECT "TenantName" FROM "Tenants" WHERE "TenantID" = {args.tenant_id}')
    if not tenant:
        sys.exit(f"No tenant with id {args.tenant_id}.")

    print(f"About to create {args.role} {email} on tenant {args.tenant_id} ({tenant}).")

    # The role the frontend actually reads is this one, not the database column.
    # auth-provider.tsx takes it from the token's app_metadata.app_role, and a user
    # without it is "User" — signed in, and shown none of the admin screens. The
    # database row decides what the API allows; this decides what the menu shows,
    # and they have to agree or the account looks broken in a way that suggests
    # nothing about the cause. app_metadata rather than user_metadata because only
    # the service role may write it: a user cannot re-tenant or promote themselves.
    body = {
        "email": email,
        "email_confirm": True,
        "app_metadata": {"app_role": args.role, "tenant_id": args.tenant_id},
    }
    if args.no_password:
        # No credential is chosen here at all. The account exists and is confirmed;
        # the person it belongs to sets the password themselves through the normal
        # reset flow, so nobody else ever knows it.
        print("No password set. Use Forgot password on the login page to choose one.")
    else:
        password = getpass.getpass("Password for the new account (not echoed): ")
        if len(password) < 12:
            sys.exit("Refusing a password under 12 characters for a super admin account.")
        if password != getpass.getpass("Repeat it: "):
            sys.exit("They did not match. Nothing was created.")
        body["password"] = password

    # Supabase first, because it is the system of record for authentication and
    # the local row is meaningless without an id to point at.
    created = supabase(env, "POST", "/auth/v1/admin/users", body)
    supabase_id = created.get("id")
    if not supabase_id:
        sys.exit(f"Supabase did not return an id: {json.dumps(created)[:200]}")
    print(f"Supabase user created: {supabase_id}")

    # If this fails the Supabase user is removed again, so a retry is clean
    # rather than colliding with a half-made account from the attempt before.
    try:
        # PasswordHash is a leftover. SupabaseAuthMigration drops it, but that
        # migration has never been applied to production — and must not be, because
        # it truncates the User table on the way past. Entity Framework no longer
        # maps the column, so nothing reads this value; it exists only to satisfy a
        # NOT NULL constraint that should have gone years ago. The text says so, so
        # that whoever finds it in the table is not left wondering what it hashes.
        psql(
            db,
            f"""
            INSERT INTO "User" ("SupabaseUserId", "TenantID", "Email", "FirstName", "LastName",
                                "Role", "IsActive", "CreatedOn", "CreatedBy", "PasswordHash")
            VALUES ('{supabase_id}', {args.tenant_id}, '{email}',
                    '{args.first_name}', '{args.last_name}', '{args.role}', true, now(),
                    'seed-script', 'unused-supabase-owns-credentials')
            """,
        )
    except SystemExit:
        print("Local insert failed — removing the Supabase user so this can be retried cleanly.")
        supabase(env, "DELETE", f"/auth/v1/admin/users/{supabase_id}")
        raise

    print(f"Done. {email} is now {args.role} on tenant {args.tenant_id}.")
    print("Sign in at https://app.delicatecourier.co.za/login")


if __name__ == "__main__":
    main()
