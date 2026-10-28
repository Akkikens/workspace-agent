import assert from 'node:assert/strict';
import { splitIdentifier, tokenize } from '../../indexer/tokenize';

describe('splitIdentifier', () => {
  it('splits camelCase and PascalCase', () => {
    assert.deepEqual(splitIdentifier('parseInvoiceTotal'), ['parse', 'Invoice', 'Total']);
    assert.deepEqual(splitIdentifier('RateLimiter'), ['Rate', 'Limiter']);
  });

  it('keeps acronyms together', () => {
    assert.deepEqual(splitIdentifier('parseHTTPResponse'), ['parse', 'HTTP', 'Response']);
    assert.deepEqual(splitIdentifier('XMLParser'), ['XML', 'Parser']);
  });

  it('splits snake_case, kebab-case and $', () => {
    assert.deepEqual(splitIdentifier('hash_password'), ['hash', 'password']);
    assert.deepEqual(splitIdentifier('MAX_RETRY_COUNT'), ['MAX', 'RETRY', 'COUNT']);
    assert.deepEqual(splitIdentifier('data-source'), ['data', 'source']);
    assert.deepEqual(splitIdentifier('$scope_value'), ['scope', 'value']);
  });
});

describe('tokenize', () => {
  it('emits lowercased parts plus the compound identifier', () => {
    assert.deepEqual(tokenize('parseInvoiceTotal'), ['parse', 'invoice', 'total', 'parseinvoicetotal']);
    assert.deepEqual(tokenize('verify_password'), ['verify', 'password', 'verify_password']);
  });

  it('matches across naming conventions', () => {
    const camel = new Set(tokenize('getUserName()'));
    const snake = new Set(tokenize('get_user_name'));
    for (const t of ['get', 'user', 'name']) {
      assert.ok(camel.has(t) && snake.has(t), `both should contain ${t}`);
    }
  });

  it('drops stopwords, single characters and pure numbers', () => {
    assert.deepEqual(tokenize('return the x of 42 for a'), []);
    assert.deepEqual(tokenize('const total = 0;'), ['total']);
  });

  it('ignores punctuation', () => {
    assert.deepEqual(tokenize('items.reduce((sum, item) => sum + item.quantity)'), ['items', 'reduce', 'sum', 'item', 'sum', 'item', 'quantity']);
  });
});
