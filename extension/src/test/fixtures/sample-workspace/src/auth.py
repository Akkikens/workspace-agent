import hashlib
import hmac


def hash_password(password: str, salt: bytes) -> str:
    return hashlib.pbkdf2_hmac("sha256", password.encode(), salt, 100_000).hex()


def verify_password(password: str, salt: bytes, expected: str) -> bool:
    return hmac.compare_digest(hash_password(password, salt), expected)
