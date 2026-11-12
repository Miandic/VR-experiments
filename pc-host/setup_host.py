"""Generate private credentials and a Quest provisioning file; never commit private/."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import secrets

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import NameOID


def provision(directory, lan_host, remote_host, port):
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    if any(directory.iterdir()):
        raise ValueError("Directory must be empty; existing credentials are never overwritten")
    key = ec.generate_private_key(ec.SECP256R1())
    subject = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Quest Desktop")])
    now = datetime.datetime.now(datetime.timezone.utc)
    cert = (x509.CertificateBuilder().subject_name(subject).issuer_name(subject)
            .public_key(key.public_key()).serial_number(x509.random_serial_number())
            .not_valid_before(now - datetime.timedelta(minutes=5))
            .not_valid_after(now + datetime.timedelta(days=3650))
            .sign(key, hashes.SHA256()))
    (directory / "key.pem").write_bytes(key.private_bytes(
        serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
        serialization.NoEncryption()))
    (directory / "cert.pem").write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    token = secrets.token_urlsafe(32)
    (directory / "host.json").write_text(json.dumps({"token": token}), encoding="utf-8")
    config = dict(lanHost=lan_host, remoteHost=remote_host, port=port, token=token,
                  certificateSha256=hashlib.sha256(cert.public_bytes(serialization.Encoding.DER)).hexdigest())
    (directory / "connection.json").write_text(json.dumps(config, indent=2), encoding="utf-8")
    return config


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lan-host", required=True, help="LAN IP of this PC")
    parser.add_argument("--remote-host", default="", help="Public static IP or DNS")
    parser.add_argument("--port", type=int, default=47990)
    parser.add_argument("--directory", default=str(Path(__file__).parent / "private"))
    args = parser.parse_args()
    provision(args.directory, args.lan_host, args.remote_host, args.port)
    print("Created credentials and connection.json in", args.directory)
