#!/usr/bin/env python3
"""Checks Keycloak sign-in and registration "as in a browser": forms, cookies, PKCE — without a browser.

Scenarios (realm videoservice, client videoservice-web):
  1. demo sign-in by USERNAME, by E-MAIL and by PHONE (including "8 000 000 00 01");
  2. token contains aud=videoservice-api;
  3. registering a new user with a phone (no e-mail) → immediate sign-in;
  4. signing in as this user by username and by phone;
  5. registering again with the same phone is rejected;
  6. registering with an invalid phone is rejected;
  7. wrong password and unknown phone give the same rejection (no enumeration);
  8. redirect_uri for localhost AND 127.0.0.1 is accepted, a foreign one is not.
Run: make verify-auth   (the stack must be up)
"""
import base64
import hashlib
import html
import http.cookiejar
import json
import os
import re
import secrets
import sys
import urllib.error
import urllib.parse
import urllib.request

KC = os.environ.get("KC", "http://localhost:8080")
REALM_URL = f"{KC}/realms/videoservice/protocol/openid-connect"
CLIENT = "videoservice-web"
DEMO_USER = os.environ.get("DEMO_USER", "demo")
DEMO_PASSWORD = os.environ.get("DEMO_PASSWORD") or sys.exit("DEMO_PASSWORD is not set (run via `make verify-auth`, which loads .env)")
DEMO_PHONE = "+70000000001"

failures = 0


def ok(msg):
    print(f"  \033[32m✓\033[0m {msg}")


def bad(msg):
    global failures
    failures += 1
    print(f"  \033[31m✗ {msg}\033[0m")


def check(cond, msg):
    ok(msg) if cond else bad(msg)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


class LocalhostPolicy(http.cookiejar.DefaultCookiePolicy):
    """Keycloak sets Secure cookies even over http://localhost — browsers accept them, the default cookiejar does not."""

    def return_ok_secure(self, cookie, request):
        return True

    def set_ok_secure(self, cookie, request):
        return True


class Session:
    """One "browser": its own cookie jar, manual redirects."""

    def __init__(self):
        self.jar = http.cookiejar.CookieJar(policy=LocalhostPolicy())
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar), NoRedirect)

    def request(self, url, data=None):
        body = urllib.parse.urlencode(data).encode() if data is not None else None
        req = urllib.request.Request(url, data=body, headers={"Accept-Language": "ru"})
        try:
            r = self.opener.open(req, timeout=20)
            return r.status, dict(r.headers), r.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as e:
            return e.code, dict(e.headers), e.read().decode("utf-8", "replace")


def pkce():
    verifier = secrets.token_urlsafe(48)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    return verifier, challenge


def auth_url(redirect_uri, challenge, registrations=False):
    q = urllib.parse.urlencode({
        "client_id": CLIENT, "response_type": "code", "scope": "openid profile email",
        "redirect_uri": redirect_uri, "state": secrets.token_hex(8),
        "code_challenge": challenge, "code_challenge_method": "S256",
    })
    return f"{REALM_URL}/{'registrations' if registrations else 'auth'}?{q}"


def form_of(page, form_id=None):
    pattern = rf'<form[^>]*id="{form_id}"[^>]*>' if form_id else r"<form[^>]*>"
    m = re.search(pattern, page)
    if not m:
        return None, {}
    tag = m.group(0)
    action = html.unescape(re.search(r'action="([^"]*)"', tag).group(1))
    hidden = {n: html.unescape(v) for n, v in re.findall(r'<input[^>]*type="hidden"[^>]*name="([^"]+)"[^>]*value="([^"]*)"', page)}
    return action, hidden


def exchange(code, verifier, redirect_uri):
    s = Session()
    status, _, body = s.request(f"{REALM_URL}/token", {
        "grant_type": "authorization_code", "client_id": CLIENT, "code": code,
        "redirect_uri": redirect_uri, "code_verifier": verifier})
    return status, json.loads(body)


def jwt_payload(token):
    part = token.split(".")[1]
    return json.loads(base64.urlsafe_b64decode(part + "=" * (-len(part) % 4)))


def login(identifier, password, redirect_uri="http://localhost:3000/auth/callback"):
    """Returns (access_token | None, error page | None)."""
    s = Session()
    verifier, challenge = pkce()
    status, _, page = s.request(auth_url(redirect_uri, challenge))
    if status != 200:
        return None, f"auth endpoint: HTTP {status}: " + re.sub(r"<[^>]+>", " ", page)
    action, hidden = form_of(page, "kc-form-login")
    if not action:
        return None, "login form not found"
    status, headers, page = s.request(action, {**hidden, "username": identifier, "password": password})
    location = headers.get("Location", "")
    if status == 302 and location.startswith(redirect_uri):
        code = urllib.parse.parse_qs(urllib.parse.urlparse(location).query)["code"][0]
        st, token = exchange(code, verifier, redirect_uri)
        return (token.get("access_token") if st == 200 else None), (None if st == 200 else f"token: {token}")
    return None, page


def register(fields, redirect_uri="http://localhost:3000/auth/callback"):
    """Returns (access_token | None, page)."""
    s = Session()
    verifier, challenge = pkce()
    status, _, page = s.request(auth_url(redirect_uri, challenge, registrations=True))
    if status != 200:
        return None, f"registrations endpoint: HTTP {status}: {page[:200]}"
    action, hidden = form_of(page, "kc-register-form")
    if not action:
        return None, "registration form not found"
    status, headers, page = s.request(action, {**hidden, **fields})
    location = headers.get("Location", "")
    if status == 302 and location.startswith(redirect_uri):
        code = urllib.parse.parse_qs(urllib.parse.urlparse(location).query)["code"][0]
        st, token = exchange(code, verifier, redirect_uri)
        return (token.get("access_token") if st == 200 else None), page
    return None, page


def is_rejected(page):
    return page is not None and ('kc-error-message' in page or 'pf-m-error' in page or 'error' in page.lower())


def main():
    suffix = secrets.token_hex(3)
    new_user = f"viewer{suffix}"
    new_phone = f"+7999{int(suffix, 16) % 10_000_000:07d}"
    password = "Str0ng-pass-" + suffix

    print("\n▸ Demo user sign-in three ways")
    for label, ident in [("username", DEMO_USER), ("e-mail", "demo@videoservice.local"),
                         ("phone +70000000001", DEMO_PHONE), ("phone 8 000 000 00 01", "8 000 000 00 01")]:
        token, err = login(ident, DEMO_PASSWORD)
        check(token is not None, f"sign-in by {label}" + ("" if token else f": {str(err)[:120]}"))
        if token and label == "username":
            payload = jwt_payload(token)
            aud = payload.get("aud")
            check("videoservice-api" in (aud if isinstance(aud, list) else [aud]), f"aud contains videoservice-api ({aud})")
            check(payload.get("preferred_username") == DEMO_USER, "preferred_username = demo")

    print("\n▸ Registration with a phone (no e-mail)")
    token, page = register({"username": new_user, "phoneNumber": new_phone, "email": "",
                            "password": password, "password-confirm": password})
    check(token is not None, f"registration {new_user} / {new_phone}" + ("" if token else f": {re.sub(r'<[^>]+>', ' ', page)[:300]}"))

    print("\n▸ New user sign-in")
    for label, ident in [("username", new_user), ("phone", new_phone), ("phone without '+'", new_phone[1:])]:
        token, err = login(ident, password)
        check(token is not None, f"sign-in by {label}" + ("" if token else f": {str(err)[:120]}"))

    print("\n▸ Rejections")
    other = f"other{suffix}"
    token, page = register({"username": other, "phoneNumber": new_phone, "email": "",
                            "password": password, "password-confirm": password})
    text = re.sub(r"<[^>]+>", " ", page or "")
    check(token is None and "already registered" in text, "same phone rejected at registration (\"already registered\")")

    token, page = register({"username": f"bad{suffix}", "phoneNumber": "89991234567", "email": "",
                            "password": password, "password-confirm": password})
    text = re.sub(r"<[^>]+>", " ", page or "")
    check(token is None and "international format" in text, "phone not in E.164 format rejected")

    t1, page1 = login(new_phone, "wrong-password")
    t2, page2 = login("+79990000000", "wrong-password")
    msg = lambda p: re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", p or "")).strip()
    m1 = re.search(r"Invalid[^.]*\.", msg(page1))
    m2 = re.search(r"Invalid[^.]*\.", msg(page2))
    check(t1 is None and t2 is None and m1 and m2 and m1.group(0) == m2.group(0),
          "wrong password and unknown phone give the same message")

    print("\n▸ redirect_uri")
    for host in ("localhost", "127.0.0.1"):
        for port in (3000, 5173):
            uri = f"http://{host}:{port}/auth/callback"
            token, err = login(DEMO_USER, DEMO_PASSWORD, uri)
            check(token is not None, f"{uri} accepted" + ("" if token else f": {str(err)[:100]}"))
    token, err = login(DEMO_USER, DEMO_PASSWORD, "http://evil.example/auth/callback")
    check(token is None and "redirect_uri" in str(err), "foreign redirect_uri rejected")

    print()
    if failures:
        print(f"\033[31mFailed checks: {failures}\033[0m")
        sys.exit(1)
    print("\033[32mSign-in and registration work.\033[0m")


if __name__ == "__main__":
    main()

