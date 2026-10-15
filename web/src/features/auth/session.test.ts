import { beforeEach, describe, expect, it } from 'vitest';
import { defaults } from '@immich/sdk';
import {
  activateAccount,
  forgetAccount,
  getActiveAccount,
  listAccounts,
  parseSessionStore,
  recordLogin,
} from './session';
import type { LoginResponseDto } from '@immich/sdk';

/**
 * Two things are worth testing here: the parser (untrusted input, all the
 * decisions) and the store transitions (upsert, activate, forget), observed
 * through both localStorage and the SDK header they are supposed to drive.
 * The React components around them are composition and get no tests.
 */

function loginDto(email: string, token: string): LoginResponseDto {
  return {
    accessToken: token,
    isAdmin: false,
    isOnboarded: true,
    name: email.split('@')[0] ?? email,
    profileImagePath: '',
    shouldChangePassword: false,
    userEmail: email,
    userId: `id-${email}`,
  };
}

beforeEach(() => {
  localStorage.clear();
  delete defaults.headers.Authorization;
});

describe('parseSessionStore', () => {
  it('returns an empty store for absent, empty or malformed input', () => {
    expect(parseSessionStore(null)).toEqual({ accounts: [], activeEmail: null });
    expect(parseSessionStore('')).toEqual({ accounts: [], activeEmail: null });
    expect(parseSessionStore('{not json')).toEqual({ accounts: [], activeEmail: null });
    expect(parseSessionStore('"a string"')).toEqual({ accounts: [], activeEmail: null });
  });

  it('drops corrupt accounts individually instead of discarding the store', () => {
    const raw = JSON.stringify({
      accounts: [
        { email: 'a@x.com', name: 'A', userId: 'u1', token: 't1' },
        { email: 'b@x.com', name: 'B' }, // no token: unusable, dropped
        'garbage',
      ],
      activeEmail: 'a@x.com',
    });
    expect(parseSessionStore(raw)).toEqual({
      accounts: [{ email: 'a@x.com', name: 'A', userId: 'u1', token: 't1' }],
      activeEmail: 'a@x.com',
    });
  });

  it('clears activeEmail when it names an account that did not survive', () => {
    const raw = JSON.stringify({
      accounts: [{ email: 'a@x.com', name: 'A', userId: 'u1', token: 't1' }],
      activeEmail: 'gone@x.com',
    });
    expect(parseSessionStore(raw).activeEmail).toBeNull();
  });
});

describe('store transitions', () => {
  it('recordLogin stores the account, activates it and sets the bearer header', () => {
    recordLogin(loginDto('a@x.com', 'token-a'));

    expect(getActiveAccount()?.email).toBe('a@x.com');
    expect(defaults.headers.Authorization).toBe('Bearer token-a');
  });

  it('recordLogin upserts by email rather than duplicating', () => {
    recordLogin(loginDto('a@x.com', 'old-token'));
    recordLogin(loginDto('a@x.com', 'new-token'));

    expect(listAccounts()).toHaveLength(1);
    expect(getActiveAccount()?.token).toBe('new-token');
    expect(defaults.headers.Authorization).toBe('Bearer new-token');
  });

  it('activateAccount switches the header to the stored token', () => {
    recordLogin(loginDto('a@x.com', 'token-a'));
    recordLogin(loginDto('b@x.com', 'token-b'));

    const switched = activateAccount('a@x.com');

    expect(switched?.email).toBe('a@x.com');
    expect(getActiveAccount()?.email).toBe('a@x.com');
    expect(defaults.headers.Authorization).toBe('Bearer token-a');
  });

  it('activateAccount returns null for an unknown email and changes nothing', () => {
    recordLogin(loginDto('a@x.com', 'token-a'));

    expect(activateAccount('nobody@x.com')).toBeNull();
    expect(getActiveAccount()?.email).toBe('a@x.com');
    expect(defaults.headers.Authorization).toBe('Bearer token-a');
  });

  it('forgetAccount removes the active account and clears the header', () => {
    recordLogin(loginDto('a@x.com', 'token-a'));
    forgetAccount('a@x.com');

    expect(listAccounts()).toHaveLength(0);
    expect(getActiveAccount()).toBeNull();
    expect(defaults.headers.Authorization).toBeUndefined();
  });

  it('forgetAccount leaves the header alone when removing an inactive account', () => {
    recordLogin(loginDto('a@x.com', 'token-a'));
    recordLogin(loginDto('b@x.com', 'token-b'));

    forgetAccount('a@x.com');

    expect(listAccounts().map((account) => account.email)).toEqual(['b@x.com']);
    expect(defaults.headers.Authorization).toBe('Bearer token-b');
  });
});
