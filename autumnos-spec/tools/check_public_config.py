#!/usr/bin/env python3
"""Validate this handoff's public config only; no network or product tests."""
from __future__ import annotations
import json
from pathlib import Path
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parents[1]

def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)

def walk(value: object) -> None:
    forbidden = {'clientsecret','client_secret','password','refreshtoken','refresh_token',
                 'accesstoken','access_token','privatekey','private_key'}
    if isinstance(value, dict):
        for key, item in value.items():
            require(str(key).lower() not in forbidden, f'Secret field is not allowed: {key}')
            walk(item)
    elif isinstance(value, list):
        for item in value:
            walk(item)

def main() -> None:
    raw = json.loads((ROOT/'config/logto.public.json').read_text(encoding='utf-8'))
    walk(raw)
    cfg = raw['Logto']
    require(cfg['ClientId'] == 'tjck5m8ohjkw272y42adv', 'Client ID differs from supplied screenshot')
    require(len(cfg['ClientId']) == 21, 'Unexpected client ID length')
    require(cfg['Endpoint'] == 'https://account.labchronicles.cn/', 'Endpoint mismatch')
    require(cfg['Authority'] == 'https://account.labchronicles.cn/oidc', 'Authority mismatch')
    require(cfg['MetadataAddress'] == cfg['Authority']+'/.well-known/openid-configuration',
            'Discovery address mismatch')
    for key, path in [('RedirectUri','/callback/'),
                      ('PostLogoutRedirectUri','/logout-callback/')]:
        u=urlparse(cfg[key])
        require(u.scheme=='http' and u.hostname=='127.0.0.1' and u.port==17853
                and u.path==path and not u.query and not u.fragment
                and u.username is None and u.password is None,
                f'{key} is not the agreed loopback baseline')
    require(cfg['ResponseType']=='code', 'Code flow is required')
    require(cfg['UsePkce'] is True and cfg['PkceMethod']=='S256', 'PKCE S256 is required')
    require(cfg['ClientAuthenticationMethod']=='none', 'Native client must not use shared secret')
    require(cfg['Scopes']==['openid','profile'], 'Default scopes changed')
    require(cfg['RememberSignInAdditionalScopes']==['offline_access'], 'Remember-sign-in scopes changed')
    status=json.loads((ROOT/'config/logto.registration-status.json').read_text(encoding='utf-8'))
    require(status['application_id']==cfg['ClientId'], 'Registration status client ID mismatch')
    print(json.dumps({
        'public_config_structure':'passed',
        'checks_are_specific_to_this_handoff':True,
        'remote_logto_contacted_by_this_script':False,
        'application_registration_verified_by_this_script':False,
        'login_test_executed_by_this_script':False,
        'meaning':'Only static field consistency was checked; registration and real login remain unverified.'
    },ensure_ascii=False,indent=2))

if __name__=='__main__':
    main()
