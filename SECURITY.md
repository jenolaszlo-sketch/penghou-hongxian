# Security policy

Penghou.Hongxian is currently pre-release. Please report suspected
vulnerabilities privately through GitHub's security advisory feature rather
than opening a public issue.

Hongxian provides tamper-evident history through Penghou.Siming. It does not
provide access control, actor authentication, secret storage, whole-database
rollback detection, or protection against an administrator replacing all local
state. Hosts must authenticate actors, authorize actions, protect files, and
anchor trusted checkpoints when rollback detection is required.

Do not place credentials, unrestricted prompts, model responses, or other
secrets in immutable payloads. Use the sensitivity and retention fields before
append; committed history cannot be redacted without replacing the ledger.
