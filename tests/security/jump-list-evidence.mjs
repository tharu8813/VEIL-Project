import { createHash } from 'node:crypto';
import { readdir, readFile } from 'node:fs/promises';
import { join } from 'node:path';

export async function snapshotJumpLists(recentFolder) {
    const contents = new Map();
    for (const kind of ['AutomaticDestinations', 'CustomDestinations']) {
        const folder = join(recentFolder, kind);
        const names = await readdir(folder, { withFileTypes: true }).catch(error => {
            if (error.code === 'ENOENT') return [];
            throw error;
        });
        for (const entry of names) {
            if (!entry.isFile() || !/^[a-f0-9]{16}\.(automatic|custom)Destinations-ms$/i.test(entry.name)) continue;
            const bytes = await readFile(join(folder, entry.name));
            // Keep the bytes transient. The report contains flags, never text from local Jump Lists.
            const digest = createHash('sha256').update(bytes).digest('hex');
            contents.set(`${kind}/${entry.name}`, { bytes, digest });
        }
    }
    return contents;
}

export function inspectJumpListChanges(before, after, { profile, fixtureHost, visitTitleMarker }) {
    const changed = [];
    for (const [file, { bytes, digest }] of after) {
        if (before.get(file)?.digest === digest) continue;
        // Shell-link payloads need not start on an even byte boundary inside a container.
        const encodings = [bytes.toString('utf8'), bytes.toString('utf16le'), bytes.subarray(1).toString('utf16le')]
            .map(text => text.toLowerCase());
        const contains = marker => encodings.some(text => text.includes(marker.toLowerCase()));
        changed.push({
            file, bytes: bytes.length,
            mentionsProfile: contains(profile),
            fixtureHost: contains(fixtureHost),
            visitTitle: contains(visitTitleMarker),
        });
    }
    return changed;
}
