/**
 * Anatomia 3D - submission file gateway
 *
 *   Unity  ->  this Worker  ->  Cloudflare R2
 *
 * Unity never holds an R2 credential. It sends the signed-in user's Firebase ID
 * token, and this Worker:
 *
 *   1. verifies that token's RS256 signature against Google's public JWKs, plus
 *      its issuer / audience / expiry, and takes the uid from `sub` ONLY.
 *      studentId / teacherId are never read from the request body or headers -
 *      a tampered client cannot upload or download as someone else.
 *   2. re-reads `quizzes/{quizId}` and `classrooms/{classroomId}` from Firestore
 *      using that same user token, so Firestore security rules apply on top of
 *      everything checked here.
 *   3. re-checks, server-side: the quiz really is a file-submission assignment,
 *      the quiz is published to that classroom, the student is enrolled (or the
 *      teacher owns the classroom), the deadline has not passed, the attempt
 *      limit is not used up, and the file's extension and size are within what
 *      the teacher configured.
 *
 * Only then does it put the object in R2, under:
 *
 *   assignments/{classroomId}/{quizId}/submissions/{studentId}/{submissionId}/{fileName}
 *
 * Routes
 *   POST /v1/submissions/upload      body = raw file bytes
 *   GET  /v1/submissions/download?key=...
 *   GET  /v1/health
 */

const JWK_URL = 'https://www.googleapis.com/service_accounts/v1/jwk/securetoken@system.gserviceaccount.com';
const FIRESTORE_ROOT = 'https://firestore.googleapis.com/v1';

// Absolute ceiling regardless of what a quiz doc says, so a malformed
// `maxFileSizeMB` can never be used to fill the bucket.
const HARD_MAX_BYTES = 50 * 1024 * 1024;

let jwkCache = { keys: null, fetchedAt: 0 };

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method === 'OPTIONS') return corsResponse(new Response(null, { status: 204 }));
    if (url.pathname === '/v1/health') return corsResponse(json({ ok: true }));

    try {
      if (url.pathname === '/v1/submissions/upload' && request.method === 'POST') {
        return corsResponse(await handleUpload(request, env));
      }

      if (url.pathname === '/v1/submissions/download' && request.method === 'GET') {
        return corsResponse(await handleDownload(request, env, url));
      }

      return corsResponse(fail(404, 'not_found', 'Unknown endpoint.'));
    } catch (err) {
      console.error('Unhandled error', err && err.stack ? err.stack : err);
      return corsResponse(fail(500, 'internal_error', 'The file server hit an unexpected error. Please try again.'));
    }
  }
};

/* ============================================================
   Upload
   ============================================================ */

async function handleUpload(request, env) {
  const auth = await authenticate(request, env);
  if (auth.error) return auth.error;
  const uid = auth.uid;
  const idToken = auth.idToken;

  const classroomId = cleanId(request.headers.get('X-Anatomia-Classroom-Id'));
  const quizId = cleanId(request.headers.get('X-Anatomia-Quiz-Id'));
  const submissionId = cleanId(request.headers.get('X-Anatomia-Submission-Id'));
  const rawFileName = safeDecode(request.headers.get('X-Anatomia-File-Name'));
  const fileName = sanitizeFileName(rawFileName);

  if (!classroomId || !quizId || !submissionId || !fileName) {
    return fail(400, 'bad_request', 'The upload request was incomplete. Please try again.');
  }

  // ---- quiz ----
  const quizDoc = await getFirestoreDoc(env, idToken, `quizzes/${quizId}`);
  if (!quizDoc) return fail(404, 'quiz_not_found', 'This assignment no longer exists.');

  const quiz = readQuiz(quizDoc);
  if (quiz.submissionType !== 'file') {
    return fail(400, 'not_file_assignment', 'This assignment does not accept file submissions.');
  }

  // ---- classroom: enrolment + publication ----
  const classroomDoc = await getFirestoreDoc(env, idToken, `classrooms/${classroomId}`);
  if (!classroomDoc) return fail(404, 'classroom_not_found', 'This classroom no longer exists.');

  const classroom = readClassroom(classroomDoc);

  if (!classroom.memberIds.includes(uid)) {
    return fail(403, 'not_enrolled', 'You are not enrolled in this classroom.');
  }

  if (classroom.publishedQuizIds.length > 0 && !classroom.publishedQuizIds.includes(quizId)) {
    return fail(403, 'not_published', 'This assignment is not available in this classroom.');
  }

  // ---- deadline ----
  if (quiz.isDeadlineEnabled && quiz.deadlineMs && Date.now() > quiz.deadlineMs) {
    return fail(403, 'deadline_passed', 'The deadline for this assignment has passed. You can no longer submit.');
  }

  // ---- attempts ----
  if (quiz.maxAttempts > 0) {
    const used = await countSubmissions(env, idToken, uid, quizId, classroomId);
    if (used >= quiz.maxAttempts) {
      return fail(403, 'no_attempts_left', 'You have used all available attempts for this assignment.');
    }
  }

  // ---- file type ----
  const extension = extensionOf(fileName);
  if (!extension || !quiz.allowedExtensions.includes(extension)) {
    const list = quiz.allowedExtensions.map((e) => e.toUpperCase()).join(', ') || 'none';
    return fail(415, 'file_type_not_allowed', `Only ${list} files are accepted for this assignment.`);
  }

  // ---- size ----
  const maxBytes = Math.min(quiz.maxFileSizeMB * 1024 * 1024, HARD_MAX_BYTES);
  const declared = Number(request.headers.get('Content-Length') || 0);
  if (declared > maxBytes) {
    return fail(413, 'file_too_large', `That file is larger than the ${quiz.maxFileSizeMB} MB limit for this assignment.`);
  }

  const bytes = new Uint8Array(await request.arrayBuffer());
  if (bytes.byteLength === 0) {
    return fail(400, 'empty_file', 'That file is empty. Please choose a different file.');
  }
  // Re-check against the real byte count - Content-Length is client-supplied.
  if (bytes.byteLength > maxBytes) {
    return fail(413, 'file_too_large', `That file is larger than the ${quiz.maxFileSizeMB} MB limit for this assignment.`);
  }

  // uid comes from the verified token, so the key can only ever land under the
  // caller's own submission prefix.
  const storageKey = `assignments/${classroomId}/${quizId}/submissions/${uid}/${submissionId}/${fileName}`;
  const contentType = request.headers.get('Content-Type') || 'application/octet-stream';

  await env.SUBMISSIONS_BUCKET.put(storageKey, bytes, {
    httpMetadata: { contentType, contentDisposition: `attachment; filename="${asciiFallback(fileName)}"` },
    customMetadata: {
      studentId: uid,
      quizId,
      classroomId,
      submissionId,
      originalFileName: fileName,
      uploadedAt: new Date().toISOString()
    }
  });

  return json({
    storageKey,
    fileSize: bytes.byteLength,
    mimeType: contentType
  });
}

/* ============================================================
   Download
   ============================================================ */

async function handleDownload(request, env, url) {
  const auth = await authenticate(request, env);
  if (auth.error) return auth.error;
  const uid = auth.uid;
  const idToken = auth.idToken;

  const key = url.searchParams.get('key') || '';
  const parsed = parseStorageKey(key);
  if (!parsed) return fail(400, 'bad_key', 'That file reference is not valid.');

  // A student may only ever read their own submission. Anyone else has to be
  // the teacher who owns the classroom it belongs to - which is re-read from
  // Firestore here rather than trusted from the request.
  if (parsed.studentId !== uid) {
    const classroomDoc = await getFirestoreDoc(env, idToken, `classrooms/${parsed.classroomId}`);
    if (!classroomDoc) return fail(403, 'forbidden', 'You are not allowed to open this file.');

    const classroom = readClassroom(classroomDoc);
    if (classroom.teacherId !== uid) {
      return fail(403, 'forbidden', 'You are not allowed to open this file.');
    }
  }

  const object = await env.SUBMISSIONS_BUCKET.get(key);
  if (!object) return fail(404, 'file_not_found', 'That file is no longer stored.');

  const headers = new Headers();
  object.writeHttpMetadata(headers);
  headers.set('etag', object.httpEtag);
  headers.set('Cache-Control', 'private, no-store');
  if (!headers.has('Content-Disposition')) {
    headers.set('Content-Disposition', `attachment; filename="${asciiFallback(parsed.fileName)}"`);
  }

  return new Response(object.body, { status: 200, headers });
}

/* ============================================================
   Firebase ID token verification
   ============================================================ */

async function authenticate(request, env) {
  const header = request.headers.get('Authorization') || '';
  if (!header.startsWith('Bearer ')) {
    return { error: fail(401, 'missing_token', 'You are not signed in. Please sign in and try again.') };
  }

  const idToken = header.slice('Bearer '.length).trim();
  const claims = await verifyFirebaseIdToken(idToken, env.FIREBASE_PROJECT_ID);
  if (!claims) {
    return { error: fail(401, 'invalid_token', 'Your session expired. Please sign in again.') };
  }

  return { uid: claims.sub, idToken };
}

async function verifyFirebaseIdToken(token, projectId) {
  const parts = token.split('.');
  if (parts.length !== 3) return null;

  let header;
  let payload;
  try {
    header = JSON.parse(utf8(base64UrlToBytes(parts[0])));
    payload = JSON.parse(utf8(base64UrlToBytes(parts[1])));
  } catch {
    return null;
  }

  if (header.alg !== 'RS256' || !header.kid) return null;

  const now = Math.floor(Date.now() / 1000);
  if (typeof payload.exp !== 'number' || payload.exp <= now) return null;
  if (typeof payload.iat !== 'number' || payload.iat > now + 300) return null;
  if (payload.aud !== projectId) return null;
  if (payload.iss !== `https://securetoken.google.com/${projectId}`) return null;
  if (!payload.sub || typeof payload.sub !== 'string') return null;

  const jwk = await getJwk(header.kid);
  if (!jwk) return null;

  const key = await crypto.subtle.importKey(
    'jwk',
    jwk,
    { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' },
    false,
    ['verify']
  );

  const valid = await crypto.subtle.verify(
    'RSASSA-PKCS1-v1_5',
    key,
    base64UrlToBytes(parts[2]),
    new TextEncoder().encode(`${parts[0]}.${parts[1]}`)
  );

  return valid ? payload : null;
}

async function getJwk(kid) {
  const oneHour = 60 * 60 * 1000;
  if (!jwkCache.keys || Date.now() - jwkCache.fetchedAt > oneHour) {
    const response = await fetch(JWK_URL, { cf: { cacheTtl: 3600 } });
    if (!response.ok) return null;
    const body = await response.json();
    jwkCache = { keys: body.keys || [], fetchedAt: Date.now() };
  }

  return jwkCache.keys.find((k) => k.kid === kid) || null;
}

/* ============================================================
   Firestore REST (as the calling user - rules still apply)
   ============================================================ */

async function getFirestoreDoc(env, idToken, path) {
  const url = `${FIRESTORE_ROOT}/projects/${env.FIREBASE_PROJECT_ID}/databases/(default)/documents/${path}`;
  const response = await fetch(url, { headers: { Authorization: `Bearer ${idToken}` } });
  if (!response.ok) return null;
  return response.json();
}

async function countSubmissions(env, idToken, studentId, quizId, classroomId) {
  const url = `${FIRESTORE_ROOT}/projects/${env.FIREBASE_PROJECT_ID}/databases/(default)/documents:runQuery`;

  const body = {
    structuredQuery: {
      from: [{ collectionId: 'fileSubmissions' }],
      where: {
        compositeFilter: {
          op: 'AND',
          filters: [
            fieldEquals('studentId', studentId),
            fieldEquals('quizId', quizId),
            fieldEquals('classroomId', classroomId)
          ]
        }
      },
      limit: 100
    }
  };

  const response = await fetch(url, {
    method: 'POST',
    headers: { Authorization: `Bearer ${idToken}`, 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  });

  if (!response.ok) {
    // Fail closed on the attempt check rather than silently allowing an extra
    // submission - Firestore rules and the client both still guard this, but a
    // read failure here must not become a free attempt.
    console.warn('countSubmissions failed', response.status, await response.text());
    return Number.MAX_SAFE_INTEGER;
  }

  const rows = await response.json();
  return rows.filter((row) => row && row.document).length;
}

function fieldEquals(field, value) {
  return {
    fieldFilter: { field: { fieldPath: field }, op: 'EQUAL', value: { stringValue: value } }
  };
}

/* ============================================================
   Firestore document readers
   ============================================================ */

function readQuiz(doc) {
  const f = doc.fields || {};
  const settings = (f.fileSubmissionSettings && f.fileSubmissionSettings.mapValue && f.fileSubmissionSettings.mapValue.fields) || {};

  const allowedExtensions = arrayOfStrings(settings.allowedExtensions).map((e) =>
    String(e).trim().replace(/^\./, '').toLowerCase()
  );

  const maxFileSizeMB = settings.maxFileSizeMB ? Number(settings.maxFileSizeMB.integerValue || settings.maxFileSizeMB.doubleValue || 25) : 25;

  return {
    submissionType: f.submissionType ? String(f.submissionType.stringValue || '').toLowerCase() : 'question',
    classroomId: f.classroomId ? f.classroomId.stringValue || '' : '',
    maxAttempts: f.maxAttempts ? Number(f.maxAttempts.integerValue || 0) : 0,
    isDeadlineEnabled: f.isDeadlineEnabled ? Boolean(f.isDeadlineEnabled.booleanValue) : false,
    deadlineMs: f.deadline && f.deadline.timestampValue ? Date.parse(f.deadline.timestampValue) : 0,
    allowedExtensions,
    maxFileSizeMB: Number.isFinite(maxFileSizeMB) && maxFileSizeMB > 0 ? maxFileSizeMB : 25
  };
}

function readClassroom(doc) {
  const f = doc.fields || {};
  return {
    teacherId: f.teacherId ? f.teacherId.stringValue || '' : '',
    memberIds: arrayOfStrings(f.memberIds),
    publishedQuizIds: arrayOfStrings(f.publishedQuizIds)
  };
}

function arrayOfStrings(field) {
  if (!field || !field.arrayValue || !Array.isArray(field.arrayValue.values)) return [];
  return field.arrayValue.values.map((v) => v.stringValue).filter((v) => typeof v === 'string');
}

/* ============================================================
   Keys, names, encoding
   ============================================================ */

function parseStorageKey(key) {
  // assignments/{classroomId}/{quizId}/submissions/{studentId}/{submissionId}/{fileName}
  const parts = key.split('/');
  if (parts.length !== 7) return null;
  if (parts[0] !== 'assignments' || parts[3] !== 'submissions') return null;
  if (parts.some((p) => p.length === 0 || p === '.' || p === '..')) return null;

  return {
    classroomId: parts[1],
    quizId: parts[2],
    studentId: parts[4],
    submissionId: parts[5],
    fileName: parts[6]
  };
}

/** Firestore ids are safe characters only - anything else is a tampered request. */
function cleanId(value) {
  if (!value) return '';
  const trimmed = String(value).trim();
  return /^[A-Za-z0-9_-]{1,128}$/.test(trimmed) ? trimmed : '';
}

function safeDecode(value) {
  if (!value) return '';
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
}

/** Strips any path components and control characters, so a filename can never
 *  escape its own prefix in the object key. */
function sanitizeFileName(name) {
  if (!name) return '';
  const base = String(name).split(/[\\/]/).pop().trim();
  const cleaned = base.replace(/[\u0000-\u001f\u007f"']/g, '').replace(/\s+/g, ' ');
  return cleaned.slice(0, 180);
}

function asciiFallback(name) {
  return String(name).replace(/[^\x20-\x7e]/g, '_');
}

function extensionOf(fileName) {
  const dot = fileName.lastIndexOf('.');
  if (dot < 0 || dot === fileName.length - 1) return '';
  return fileName.slice(dot + 1).toLowerCase();
}

function base64UrlToBytes(value) {
  const padded = value.replace(/-/g, '+').replace(/_/g, '/');
  const binary = atob(padded + '='.repeat((4 - (padded.length % 4)) % 4));
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

function utf8(bytes) {
  return new TextDecoder().decode(bytes);
}

/* ============================================================
   Responses
   ============================================================ */

function json(body, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' }
  });
}

/** Every rejection carries a stable `error` code plus a `message` the Unity
 *  client shows verbatim (see R2FileUploadService.ExtractWorkerError). */
function fail(status, error, message) {
  return json({ error, message }, status);
}

function corsResponse(response) {
  const headers = new Headers(response.headers);
  headers.set('Access-Control-Allow-Origin', '*');
  headers.set('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
  headers.set(
    'Access-Control-Allow-Headers',
    'Authorization, Content-Type, X-Anatomia-Classroom-Id, X-Anatomia-Quiz-Id, X-Anatomia-Submission-Id, X-Anatomia-File-Name'
  );
  return new Response(response.body, { status: response.status, headers });
}
