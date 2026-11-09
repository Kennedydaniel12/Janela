// The chart groups usage by the Windows local day of week and hour.
function visitLocalHours(row, visit) {
  for (let at = row.start; at < row.end;) {
    const local = new Date(at);
    const day = (local.getDay() + 6) % 7;
    const elapsedInHour = (local.getMinutes() * 60 + local.getSeconds()) * 1000 + local.getMilliseconds();
    const next = Math.min(row.end, at + 3600000 - elapsedInHour);
    visit(day * 24 + local.getHours(), at, next);
    at = next;
  }
}

function heatmapRows(rows, app, category, groups) {
  const members = groups.find(group => 'group:' + group.id === app)?.apps;
  return rows.filter(row => row.state !== 'idle' &&
    (!app || (members ? members.includes(row.app) : app === 'app:' + row.app)) &&
    (!category || category === 'v:' + row.category));
}

function buildHourMap(rows) {
  const buckets = Array.from({ length: 7 }, () => Array(24).fill(0));
  for (const row of rows) {
    visitLocalHours(row, (cell, start, end) => {
      buckets[Math.floor(cell / 24)][cell % 24] += end - start;
    });
  }
  return buckets;
}

function weeklyCategoryDays(rows) {
  const days = Array.from({ length: 7 }, () => new Map());
  for (const row of rows) {
    if (row.state === 'idle') continue;
    const name = row.category || 'Sem categoria';
    visitLocalHours(row, (cell, start, end) => {
      const day = days[Math.floor(cell / 24)];
      day.set(name, (day.get(name) ?? 0) + end - start);
    });
  }
  return days;
}

function detailRows(rows, detail, groups) {
  const members = detail.kind === 'group' ? groups.find(group => group.id === detail.key)?.apps ?? [] : [];
  return rows.filter(row => row.state !== 'idle' && (
    detail.kind === 'app' ? row.app === detail.key :
    detail.kind === 'group' ? members.includes(row.app) :
    (row.category || 'Sem categoria') === detail.key));
}

function usageBuckets(rows, from, until) {
  const monthly = until - from > 31 * 86400000;
  const nextBoundary = at => {
    const date = new Date(at);
    if (monthly) return +new Date(date.getFullYear(), date.getMonth() + 1, 1);
    return +new Date(date.getFullYear(), date.getMonth(), date.getDate() + 1);
  };
  const buckets = [];
  for (let start = from; start < until;) {
    const end = Math.min(until, nextBoundary(start));
    buckets.push({ start, end, active: 0, video: 0 });
    start = end;
  }
  let first = 0;
  for (const row of rows) {
    while (first < buckets.length && buckets[first].end <= row.start) first++;
    for (let i = first; i < buckets.length && buckets[i].start < row.end; i++) {
      const bucket = buckets[i];
      bucket[row.state] += Math.max(0, Math.min(row.end, bucket.end) - Math.max(row.start, bucket.start));
    }
  }
  return { monthly, buckets };
}

function focusSessions(rows, tolerance = 120000) {
  const sessions = [];
  for (let i = 0; i < rows.length;) {
    const first = rows[i++];
    if (first.state !== 'active' || !first.project) continue;
    const session = { project: first.project, start: first.start, end: first.end,
      focused: first.end - first.start, interruptions: 0, interruptionTime: 0 };
    while (i < rows.length && rows[i].start === session.end && rows[i].state === 'active') {
      let next = rows[i];
      if (next.project !== session.project) {
        // Only bridge an interruption if this project actually resumes within the tolerance.
        let j = i;
        let end = session.end;
        while (j < rows.length && rows[j].state === 'active' && rows[j].start === end &&
               rows[j].project !== session.project && rows[j].end - session.end <= tolerance) {
          end = rows[j++].end;
        }
        next = rows[j];
        if (!next || next.state !== 'active' || next.project !== session.project || next.start !== end) break;
        session.interruptions++;
        session.interruptionTime += end - session.end;
        i = j;
      }
      session.focused += next.end - next.start;
      session.end = next.end;
      i++;
    }
    sessions.push(session);
  }
  return sessions;
}
