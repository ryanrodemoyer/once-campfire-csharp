# Deploy ONCE Campfire to a Single Google Cloud VM

This runbook guides you from a fresh Google Cloud account to a running, production-ready, HTTPS-enabled ONCE Campfire installation running on a single Compute Engine virtual machine with a persistent disk.

Everything is automated by [`bin/deploy-gcp`](../bin/deploy-gcp), which provisions resources, builds and deploys the container image, manages backups, and inspects status.

---

## Architecture: Why a Single VM?

ONCE Campfire is designed around a single-machine deployment model:
- **One Box**: The web server, Action Cable WebSocket server, in-process job workers, SQLite database, and file storage all live together on local disk.
- **Thruster & Automatic TLS**: [Thruster](https://github.com/basecamp/thruster) sits in front of the application on ports 80 and 443, proxying to Campfire on internal port 3000. When `TLS_DOMAIN` is provided, Thruster automatically provisions and renews Let's Encrypt TLS certificates, caching them on the persistent storage volume (`/rails/storage/thruster`).
- **Zero Network Latency**: SQLite database reads and writes execute against local NVMe / persistent disk with zero network round-trip overhead.

```
                          Internet
                             │
                  HTTP (:80) │ HTTPS (:443)
                             ▼
┌─────────────────────────────────────────────────────────────┐
│ Compute Engine VM (e2-micro)                                │
│                                                             │
│   ┌─────────────────────────────────────────────────────┐   │
│   │ Campfire Container                                  │   │
│   │                                                     │   │
│   │   ┌─────────────────────────────────────────────┐   │   │
│   │   │ Thruster (Port 80 & 443)                    │   │   │
│   │   │ - HTTP/2 termination & Brotli/Gzip          │   │   │
│   │   │ - Automatic Let's Encrypt ACME from DOMAIN  │   │   │
│   │   │ - Reverse proxy to :3000                    │   │   │
│   │   └──────────────────────┬──────────────────────┘   │   │
│   │                          │ :3000                    │   │
│   │   ┌──────────────────────▼──────────────────────┐   │   │
│   │   │ Campfire Application Server                 │   │   │
│   │   │ - WebApp & Action Cable (/cable)            │   │   │
│   │   │ - In-Process Resque Job Runner              │   │   │
│   │   └──────────────────────┬──────────────────────┘   │   │
│   └──────────────────────────┼──────────────────────────┘   │
│                              │ Mount: /rails/storage         │
│                              ▼                              │
│   ┌─────────────────────────────────────────────────────┐   │
│   │ Persistent Disk (/rails/storage)                    │   │
│   │ - SQLite Database (storage/db/production.sqlite3)   │   │
│   │ - Active Storage Attachments (storage/files/)       │   │
│   │ - TLS Certificate Cache (storage/thruster/)         │   │
│   │ - Daily Snapshot Schedule (Automated GCP policy)    │   │
│   └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

### Why Cloud Run Does Not Fit

Google Cloud Run is an excellent serverless platform for stateless applications, but it is fundamentally incompatible with the ONCE Campfire architecture:

1. **No Persistent Local Disk for SQLite**:
   Cloud Run instances provide only ephemeral filesystems. When an instance restarts or scales down, all local state is wiped. While Cloud Storage FUSE can be mounted to Cloud Run containers, Cloud Storage FUSE does not support POSIX byte-range file locking or memory mapping (`mmap`), both of which are required by SQLite in WAL (Write-Ahead Logging) mode. Running SQLite on Cloud Storage FUSE causes immediate database corruption and lock crashes.
2. **Single-Instance / Single-Writer Requirement**:
   Campfire uses an embedded SQLite database that permits only one writer at a time. Cloud Run scales horizontally by spinning up additional container instances. Multiple instances attempting to write to the same database causes corruption. Even with `--max-instances=1`, rolling deployments and cold-start transitions briefly run two concurrent instances, creating race conditions.
3. **CPU Throttling Stalls Background Jobs**:
   Cloud Run throttles CPU allocation to near-zero when no inbound HTTP request is actively being processed. Campfire runs background jobs in-process (message attachments, link unfurling, webhook deliveries, and Web Push notifications). On Cloud Run, background workers are paused mid-execution as soon as the HTTP response completes, delaying jobs until an unrelated visitor happens to hit the site. While "always-on" CPU allocation can be enabled, it eliminates the cost savings of serverless without fixing the disk persistence issue.

> [!NOTE]
> **Litestream as the Alternative Route**:
> If organizational policy strictly mandates running on Cloud Run or another serverless container runtime, the documented path is [Litestream](https://litestream.io/). Litestream runs as a sidecar process that continuously streams SQLite WAL frames to a Google Cloud Storage bucket and restores the database before application startup. However, this still requires strict single-instance constraints. For standard ONCE deployments, a single persistent VM is simpler, more reliable, and completely covered by Google Cloud's Always Free tier.

---

## Costs & Always Free Tier Breakdown

Running Campfire with `bin/deploy-gcp` defaults to Google Cloud's **Always Free** tier resources, allowing you to run your chat instance for **$0.00/month** (or pennies per month for snapshot growth).

| Resource | Free Tier Allowance | Campfire Default | Estimated Monthly Cost |
| :--- | :--- | :--- | :--- |
| **Compute Engine VM** | 1 non-preemptible `e2-micro` instance in `us-central1`, `us-west1`, or `us-east1` | `e2-micro` in `us-central1` | **$0.00** |
| **Persistent Disk** | 30 GB standard persistent disk (`pd-standard`) per month | 30 GB `pd-standard` | **$0.00** |
| **Static External IP** | Free while attached to a running VM instance | 1 reserved static IP (`campfire-ip`) | **$0.00** ($0.01/hr only if unattached) |
| **Artifact Registry** | 0.5 GB storage free per month; free same-region internal download | Pinned image (~150 MB compressed) | **$0.00** ($0.10/GB/month after 0.5 GB) |
| **Disk Snapshots** | Not included in free tier; billed at $0.026/GB/month for differential blocks | Daily snapshots with 14-day retention | **~$0.05 – $0.20** (only modified blocks stored) |
| **Network Egress** | 1 GB worldwide egress free per month | Chat text and attachments | **$0.00** ($0.085/GB after 1 GB) |
| **TLS Certificates** | Free via Let's Encrypt / Thruster | Automatic ACME renewal | **$0.00** |
| **Total Estimated Cost** | — | — | **$0.00 – $0.20 / month** |

---

## Prerequisites

1. **Google Cloud SDK (`gcloud`)**:
   Installed and authenticated to your Google Cloud account:
   ```bash
   gcloud auth login
   gcloud auth application-default login
   ```
2. **Google Cloud Project**:
   A GCP project with billing enabled (required by Compute Engine):
   ```bash
   gcloud projects create PROJECT_ID
   gcloud config set project PROJECT_ID
   ```
   Enable required services:
   ```bash
   gcloud services enable compute.googleapis.com artifactregistry.googleapis.com
   ```
3. **Local Docker**:
   Docker installed on your local machine to build the container image.
4. **Domain Name**:
   A domain or subdomain (e.g., `chat.example.com`) where you want to access Campfire.

---

## Configuration & Secret-Free Architecture

To prevent committing sensitive project identifiers or credentials into public source control, `bin/deploy-gcp` reads all settings with the following precedence:

1. **Command-line flags** (e.g., `--project`, `--domain`, `--region`)
2. **Environment variables** (`PROJECT_ID`, `TLS_DOMAIN`, `REGION`)
3. **External configuration file** (`~/.config/campfire/deploy-gcp.env` by default, or `--config`)
4. **Built-in defaults** (e.g., `REGION=us-central1`, `MACHINE_TYPE=e2-micro`)

### Creating Your Configuration File

Create `~/.config/campfire/deploy-gcp.env` outside the repository:

```bash
mkdir -p ~/.config/campfire
cat <<'EOF' > ~/.config/campfire/deploy-gcp.env
PROJECT_ID=PROJECT_ID
REGION=us-central1
ZONE=us-central1-a
TLS_DOMAIN=chat.example.com
EOF
```

### Secret Management on the VM

Security is paramount in ONCE Campfire:
- **No Service Account Keys**: No service account JSON keys are ever created or downloaded to your machine. The VM pulls container images from Artifact Registry using its own attached service account identity (`roles/artifactregistry.reader`).
- **Secrets Never Leave the VM**: `SECRET_KEY_BASE` and Web Push `VAPID` key pairs are generated directly on the VM during `setup` via OpenSSL and written to `/etc/campfire/campfire.env` with `chmod 600` permissions (root-only).
- **No Secrets in Metadata or Command Lines**: Secrets are never passed in command-line arguments, never stored in Compute Engine instance metadata, never logged, and never transferred over SSH. The container reads them securely via Docker's `--env-file /etc/campfire/campfire.env`.

---

## Step-by-Step Setup and Deployment

### Step 1: Verify the Plan with `--dry-run`

Before creating any cloud resources, run `setup` in `--dry-run` mode to inspect every `gcloud` and `docker` command that will be executed:

```bash
bin/deploy-gcp --dry-run setup
```

You will see the exact sequence of `gcloud` commands for Artifact Registry, IAM, static IP, firewall, snapshot schedule, and VM instance creation.

### Step 2: Run Setup

Run `setup` to provision the cloud infrastructure:

```bash
bin/deploy-gcp setup
```

What `setup` does:
1. Creates an Artifact Registry Docker repository (`campfire`) in your region.
2. Creates a dedicated service account (`campfire-runner`) with only `roles/artifactregistry.reader`.
3. Reserves a static external IPv4 address (`campfire-ip`).
4. Creates a firewall rule (`campfire-allow-http-https`) allowing ports 80 and 443 to instances tagged with `campfire-server`.
5. Creates a daily disk snapshot schedule policy with 14-day retention.
6. Launches the Compute Engine VM (`campfire-vm`, `e2-micro`, 30 GB `pd-standard` disk, tagged `campfire-server`, attached to the static IP).
7. Attaches the daily snapshot schedule to the VM's disk.
8. Connects via SSH to install Docker and initialize `/etc/campfire/campfire.env` with freshly generated cryptographic keys.

> [!NOTE]
> `bin/deploy-gcp setup` is completely **idempotent**. If you run it multiple times, it detects existing resources and skips them without making destructive modifications.

### Step 3: Configure DNS

Find the reserved static IP address:

```bash
bin/deploy-gcp status
```

In your DNS provider (e.g., Cloudflare, Namecheap, Google Domains), create an **A record**:
- **Name**: `chat` (or `@` for apex domain)
- **Type**: `A`
- **Value**: Your reserved static external IP address (e.g., `STATIC_IP`)
- **TTL**: 300 (or Auto)

Wait a few minutes for DNS propagation before proceeding to deployment so Let's Encrypt can verify the domain.

### Step 4: Deploy the Application

Build the production image locally, push it to Artifact Registry, and run the container on the VM:

```bash
bin/deploy-gcp deploy
```

The script will:
1. Build the multi-stage Docker container locally from `Dockerfile`.
2. Configure local Docker authentication for `*-docker.pkg.dev`.
3. Push the image to Artifact Registry.
4. SSH into the VM, pull the image using the VM's service account, and start the container with:
   - `--restart unless-stopped`
   - `-p 80:80 -p 443:443`
   - `-v campfire-storage:/rails/storage`
   - `--env-file /etc/campfire/campfire.env`

### Step 5: Verify and Log In

Check status and follow live logs:

```bash
bin/deploy-gcp status
bin/deploy-gcp logs
```

Once Thruster obtains the Let's Encrypt certificate, open your browser and navigate to:
`https://chat.example.com`

You will be greeted by the Campfire first-run setup wizard. Create your administrator account to finish setup!

---

## Custom Domains & Cloudflare Proxy

If you use Cloudflare to manage your DNS and protect your site, pay close attention to the SSL/TLS encryption setting:

### Cloudflare SSL/TLS Settings

In the Cloudflare dashboard:
1. Go to **SSL/TLS** -> **Overview**.
2. Set the encryption mode to **Full** or **Full (strict)**.
3. **NEVER use Flexible mode!**

> [!WARNING]
> **Why Flexible Mode Causes an Infinite Loop (`ERR_TOO_MANY_REDIRECTS`)**:
> - In **Flexible** mode, Cloudflare accepts HTTPS connections from browser clients, but connects to your origin server over unencrypted **HTTP (port 80)**.
> - Thruster on your VM detects an unencrypted HTTP request and issues a `301 Moved Permanently` redirecting the client to `https://chat.example.com`.
> - Cloudflare receives this redirect and sends another HTTP port 80 request to the origin.
> - The cycle repeats endlessly until the browser terminates the connection with an `ERR_TOO_MANY_REDIRECTS` error.
> - In **Full** or **Full (strict)** mode, Cloudflare connects to your VM over HTTPS (port 443), satisfying Thruster and completing the request successfully.

### WebSocket Support

Campfire relies on Action Cable for real-time messaging:
- In Cloudflare, navigate to **Network** settings.
- Ensure **WebSockets** is toggled **ON** (enabled by default on all Cloudflare plans).

---

## Day-2 Operations & Maintenance

### Updating Campfire

When a new version or commit is ready:

```bash
bin/deploy-gcp deploy
```

This builds the updated image, pushes it to Artifact Registry, pulls it onto the VM, and restarts the container. Your SQLite database, user uploads, and secrets in `campfire-storage` remain completely intact. Database migrations run automatically during startup.

### Backups

#### 1. Automated Daily Snapshots
The `setup` command automatically attaches a Google Cloud snapshot schedule (`campfire-daily-snapshot`). Every day at 04:00 UTC, Compute Engine captures an incremental snapshot of the persistent disk and retains it for 14 days.

#### 2. On-Demand Snapshot Backup
To take an immediate backup before applying major changes:

```bash
bin/deploy-gcp backup
```

This triggers the container's SQLite `/hooks/pre-backup` script to checkpoint the write-ahead log and snapshot the database into `storage/backups/`, then immediately invokes `gcloud compute disks snapshot`.

#### 3. Offsite Tarball Export
To download a complete standalone archive of your database and files off the cloud:

```bash
bin/deploy-gcp ssh
```

Inside the VM:
```bash
# Run backup preparation
sudo docker exec campfire /hooks/pre-backup

# Create compressed archive of storage
sudo docker run --rm \
  --user root \
  --volume campfire-storage:/rails/storage \
  --volume "$PWD":/backup \
  alpine tar czf /backup/campfire-backup.tar.gz -C /rails storage
```

Download the resulting `campfire-backup.tar.gz` to your local machine:
```bash
gcloud compute scp campfire-vm:campfire-backup.tar.gz ./campfire-backup.tar.gz --zone=us-central1-a
```

### Restoring from a Snapshot

To restore your VM to a previous snapshot state:

1. List available snapshots:
   ```bash
   gcloud compute snapshots list --filter="sourceDisk:campfire-vm"
   ```
2. Stop the running VM:
   ```bash
   gcloud compute instances stop campfire-vm --zone=us-central1-a
   ```
3. Create a new persistent disk from the chosen snapshot:
   ```bash
   gcloud compute disks create campfire-disk-restored \
     --source-snapshot=SNAPSHOT_NAME \
     --zone=us-central1-a \
     --type=pd-standard
   ```
4. Detach the current disk and attach the restored disk as the boot disk:
   ```bash
   gcloud compute instances detach-disk campfire-vm --disk=campfire-vm --zone=us-central1-a
   gcloud compute instances attach-disk campfire-vm --disk=campfire-disk-restored --boot --zone=us-central1-a
   ```
5. Start the VM:
   ```bash
   gcloud compute instances start campfire-vm --zone=us-central1-a
   ```

### Rotating Secrets

If `SECRET_KEY_BASE` or Web Push keys are compromised, they can be rotated on the VM:

> [!CAUTION]
> Rotating `SECRET_KEY_BASE` immediately invalidates all active session cookies and signs out all users. Users will need to log back in. Rotating VAPID keys requires users to re-enable push notifications.

To rotate keys:
1. Connect to the VM:
   ```bash
   bin/deploy-gcp ssh
   ```
2. Generate a new `SECRET_KEY_BASE` directly in `/etc/campfire/campfire.env`:
   ```bash
   sudo sed -i "s/^SECRET_KEY_BASE=.*/SECRET_KEY_BASE=$(openssl rand -hex 64)/" /etc/campfire/campfire.env
   ```
3. Restart the container:
   ```bash
   sudo docker restart campfire
   ```

---

## Teardown & Resource Cleanup

If you ever wish to decommission your deployment and delete all associated cloud resources:

```bash
# 1. Delete the VM instance
gcloud compute instances delete campfire-vm --zone=us-central1-a --quiet

# 2. Detach and delete the snapshot schedule
gcloud compute resource-policies delete campfire-daily-snapshot --region=us-central1 --quiet

# 3. Delete existing snapshots
for snap in $(gcloud compute snapshots list --filter="sourceDisk:campfire-vm" --format="value(name)"); do
  gcloud compute snapshots delete "$snap" --quiet
done

# 4. Release the static IP address
gcloud compute addresses delete campfire-ip --region=us-central1 --quiet

# 5. Delete the firewall rule
gcloud compute firewall-rules delete campfire-allow-http-https --quiet

# 6. Delete the Artifact Registry repository and images
gcloud artifacts repositories delete campfire --location=us-central1 --quiet

# 7. Remove IAM binding and delete service account
SA_EMAIL="campfire-runner@PROJECT_ID.iam.gserviceaccount.com"
gcloud projects remove-iam-policy-binding PROJECT_ID \
  --member="serviceAccount:${SA_EMAIL}" \
  --role="roles/artifactregistry.reader" --quiet
gcloud iam service-accounts delete "$SA_EMAIL" --quiet
```
