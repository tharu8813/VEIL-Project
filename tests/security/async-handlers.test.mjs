// An exception escaping an `async void` handler (or an async event lambda) terminates the browser.
// Every such handler in the desktop app must catch the shared RecoverableFailure set (or all exceptions),
// either in its own body or in the single method it awaits.
//   node --test tests/security/async-handlers.test.mjs
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import test from 'node:test';

const directory = resolve('apps/desktop');
// Debug-only harnesses: a failure there is the test result, not a user-facing crash.
const excluded = new Set(['BrowserWindow.Smoke.cs', 'BrowserWindow.Phase6Smoke.cs', 'EngineProbe.cs']);
const files = readdirSync(directory).filter(name => name.endsWith('.cs') && !excluded.has(name))
    .map(name => ({ name, text: readFileSync(join(directory, name), 'utf8') }));

function block(text, start) {
    // Expression body: up to the end of the statement.
    let i = start;
    while (/\s/.test(text[i])) i++;
    if (text.startsWith('=>', i)) {
        let depth = 0;
        for (let j = i; j < text.length; j++) {
            if ('({['.includes(text[j])) depth++;
            else if (')}]'.includes(text[j])) { if (depth === 0) return text.slice(i, j); depth--; }
            else if ((text[j] === ';' || text[j] === ',') && depth === 0) return text.slice(i, j);
        }
    }
    const open = text.indexOf('{', i);
    let depth = 0;
    for (let j = open; j < text.length; j++) {
        if (text[j] === '{') depth++;
        else if (text[j] === '}' && --depth === 0) return text.slice(open, j + 1);
    }
    throw new Error('unbalanced block');
}

const methods = new Map();
for (const { text } of files)
    for (const match of text.matchAll(/(?:private|internal|public|protected)[\w\s<>?,]*?\s(\w+)\s*\([^)]*\)\s*(?=\{|=>)/g))
        methods.set(match[1], block(text, match.index + match[0].length));

const guarded = body => /RecoverableFailure\.Is\(|catch\s*\(\s*Exception\s*\)|catch\s*\{/.test(body);
function safe(body) {
    if (guarded(body)) return true;
    // Otherwise every awaited call must be a method that guards itself (e.g. Find, RunAsync).
    const awaited = [...body.matchAll(/await\s+(?:[\w.]+\.)?(\w+)\s*\(/g)].map(match => match[1]);
    return awaited.length > 0 && awaited.every(name => methods.has(name) && guarded(methods.get(name)));
}

const handlers = [];
for (const { name, text } of files) {
    for (const match of text.matchAll(/async\s+void\s+(\w+)\s*\([^)]*\)\s*/g))
        handlers.push({ where: `${name}: async void ${match[1]}`, body: block(text, match.index + match[0].length) });
    for (const match of text.matchAll(/(?:\+=|,|\()\s*async\s*(?:\([^)]*\)|\w+)\s*=>\s*/g))
        handlers.push({ where: `${name}: async lambda at ${text.slice(0, match.index).split('\n').length}`,
            body: block(text, text.indexOf('=>', match.index)) });
}

test('desktop async handlers were found', () => {
    assert.ok(handlers.length >= 20, `only ${handlers.length} handlers found; the scanner may be broken`);
});

test('every async void handler and async event lambda catches recoverable failures', () => {
    const unsafe = handlers.filter(handler => !safe(handler.body)).map(handler => handler.where);
    assert.deepEqual(unsafe, [], `handlers that can terminate the browser:\n${unsafe.join('\n')}`);
});
