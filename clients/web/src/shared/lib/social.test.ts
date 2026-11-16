import { describe, expect, it } from 'vitest';
import { mergeFirstPage, mergePages } from './pages';
import { relativeTime } from './relativeTime';
import { checkComment, COMMENT_MAX, initial, memoPromise, normalizeQuery, thumbLabel } from './social';
import { commentsPath, followersPath, followingPath, likersPath, isOwnProfilePath, profilePath, videoPath } from './urls';

const now = Date.parse('2026-01-01T12:00:00Z');
const ago = (s: number) => new Date(now - s * 1000).toISOString();

describe('relativeTime', () => {
  it('just now', () => expect(relativeTime(ago(10), now)).toBe('just now'));
  it('minutes', () => expect(relativeTime(ago(5 * 60), now)).toMatch(/5.*min/));
  it('hours', () => expect(relativeTime(ago(2 * 3600), now)).toMatch(/2.*hr/));
  it('days', () => expect(relativeTime(ago(3 * 86400), now)).toMatch(/3.*day/));
  it('garbage', () => expect(relativeTime('nope', now)).toBe(''));
});

describe('pages', () => {
  it('merge skips duplicates', () =>
    expect(mergePages([{ id: 'a' }, { id: 'b' }], [{ id: 'b' }, { id: 'c' }]).map((x) => x.id)).toEqual(['a', 'b', 'c']));
  it('first page refresh keeps older', () =>
    expect(mergeFirstPage([{ id: 'b' }, { id: 'z' }], [{ id: 'n' }, { id: 'b' }]).map((x) => x.id)).toEqual(['n', 'b', 'z']));
});

describe('urls', () => {
  it('paths', () => {
    expect(profilePath('a b')).toBe('/u/a%20b');
    expect(videoPath('1')).toBe('/videos/1');
    expect(commentsPath('1')).toBe('/videos/1#comments');
  });
  it('own profile', () => {
    expect(isOwnProfilePath('/u/me', 'me')).toBe(true);
    expect(isOwnProfilePath('/u/x', 'me')).toBe(false);
    expect(isOwnProfilePath('/u/me', null)).toBe(false);
  });
});

describe('comments and search', () => {
  it('checks comment', () => {
    expect(checkComment('   ')).toBe('empty');
    expect(checkComment('hello')).toBe('ok');
    expect(checkComment('a'.repeat(COMMENT_MAX))).toBe('ok');
    expect(checkComment('a'.repeat(COMMENT_MAX + 1))).toBe('too_long');
  });
  it('normalizes query', () => {
    expect(normalizeQuery('   ')).toBe('');
    expect(normalizeQuery('  an   na ')).toBe('an na');
  });
  it('thumb label', () => {
    expect(thumbLabel('ready')).toBeNull();
    expect(thumbLabel('failed')).toBe('rejected');
    expect(thumbLabel('processing')).toBe('editing…');
  });
  it('initial', () => {
    expect(initial('anna')).toBe('A');
    expect(initial('')).toBe('?');
  });
});

describe('memoPromise', () => {
  it('calls loader once and evicts rejected', async () => {
    const store = new Map<string, Promise<number>>();
    let calls = 0;
    const load = () => Promise.resolve(++calls);
    await memoPromise(store, 'k', load);
    await memoPromise(store, 'k', load);
    expect(calls).toBe(1);
    await expect(memoPromise(store, 'e', () => Promise.reject(new Error('x')))).rejects.toThrow();
    expect(store.has('e')).toBe(false);
  });
  it('evicts values rejected by keep', async () => {
    const store = new Map<string, Promise<number | null>>();
    await memoPromise(store, 'n', () => Promise.resolve(null), (v) => v !== null);
    expect(store.has('n')).toBe(false);
  });
});

describe('follow list paths', () => {
  it('builds paths', () => {
    expect(followersPath('a')).toBe('/u/a/followers');
    expect(followingPath('a')).toBe('/u/a/following');
  });
});

describe('likers path', () => {
  it('builds path', () => expect(likersPath('v1')).toBe('/v/v1/likes'));
});
