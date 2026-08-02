# Anatomia 3D — Firestore Schema

Backing store for every "TODO: hook up your backend" comment across the
Admin/Student controllers (auth, classrooms, quizzes, notifications,
gamification). Auth (login/create account/password reset) uses **Firebase
Authentication (Email/Password)**; everything else lives in **Cloud
Firestore**.

Design principle: a few denormalized counters/arrays (student counts,
member-id arrays) are duplicated onto parent docs so the UI can read a
single document instead of running aggregation queries on every screen
load. These are updated via transactions in the service scripts, not
client-side increments.

---

## Collections

### `users/{uid}`
Thin routing doc, written once at sign-up. Lets client code (or future
Cloud Functions) figure out "is this uid an admin or a student" without
guessing which collection to read first.

| field | type | notes |
|---|---|---|
| role | string | `"admin"` \| `"student"` |
| email | string | |
| createdAt | timestamp | |

### `admins/{uid}` (doc id = Firebase Auth uid)
| field | type | notes |
|---|---|---|
| fullName | string | |
| email | string | |
| createdAt | timestamp | |
| classroomCount | number | denormalized, maintained by AdminClassroomService |
| studentCount | number | denormalized (sum of classroom studentCounts) |
| quizzesCreated | number | denormalized |

Maps to `AdminProfileController.SetProfileData()`.

### `students/{uid}` (doc id = Firebase Auth uid)
| field | type | notes |
|---|---|---|
| fullName | string | |
| email | string | |
| createdAt | timestamp | |
| level | number | |
| totalPoints | number | |
| quizzesCompleted | number | |
| badgesEarned | array\<string\> | badge ids, see `gamificationSettings` |
| enrolledClassroomIds | array\<string\> | denormalized for "my classrooms" lookups |

Maps to `StudentProfileController.SetProfileData()`,
`StudentDashboardController.SetStudentData()`,
`StudentAchievementsController`.

### `classrooms/{classroomId}` (auto id)
| field | type | notes |
|---|---|---|
| name | string | |
| description | string | |
| code | string | 8-char join code, also the doc id in `classroomCodes` |
| teacherId | string | uid of owning admin |
| teacherName | string | denormalized |
| studentCount | number | denormalized |
| memberIds | array\<string\> | student uids, enables `array-contains` queries |
| createdAt | timestamp | |

Subcollection `classrooms/{classroomId}/members/{studentId}`:
| field | type |
|---|---|
| studentName | string |
| joinedAt | timestamp |

Maps to `AdminCreateClassroomController`, `AdminClassroomCreatedController`,
`AdminClassroomDetailController`, `StudentClassroomHubController`,
`StudentClassroomController` (join flow).

### `classroomCodes/{code}` (doc id = the code itself)
| field | type | notes |
|---|---|---|
| classroomId | string | pointer, used for O(1) code lookup + uniqueness |

### `quizzes/{quizId}` (auto id)
| field | type | notes |
|---|---|---|
| title | string | |
| category | string | skeletal / muscular / nervous / cardiovascular |
| classroomId | string \| null | null = available to everyone |
| createdBy | string | admin uid |
| pointsPossible | number | |
| questions | array\<map\> | `{ text, choices: [string], correctIndex }` |
| createdAt | timestamp | |

Maps to `AdminQuizManagementController`, `StudentQuizSelectionController`.

### `quizAttempts/{attemptId}` (auto id)
| field | type | notes |
|---|---|---|
| studentId | string | |
| quizId | string | |
| quizName | string | denormalized |
| classroomId | string \| null | |
| correctCount | number | |
| incorrectCount | number | |
| pointsEarned | number | |
| pointsPossible | number | |
| bonusXp | number | |
| percent | number | |
| completedAt | timestamp | |

Maps to `StudentQuizResultController.SetResult()`, and rolls up into
`StudentProgressController` (weekly chart / category breakdown) via
aggregation queries on this collection.

### `students/{uid}/notifications/{notificationId}` (subcollection)
| field | type | notes |
|---|---|---|
| title | string | |
| message | string | |
| icon | string | `quiz` / `achievement` / `classroom` / `system` |
| isRead | boolean | |
| createdAt | timestamp | |

Maps directly to `StudentNotificationsController.SetNotifications()`.

### `gamificationSettings/config` (singleton doc)
| field | type | notes |
|---|---|---|
| pointsPerCorrectAnswer | number | |
| levelThresholds | array\<number\> | points required per level |
| badgeDefinitions | map | badgeId -> `{ name, description, targetPoints }` |

Maps to `AdminGamificationSettingsController`.

---

## Why denormalize `memberIds` and `enrolledClassroomIds`?

Firestore can't query "classrooms whose `members` subcollection contains
uid X" directly. Keeping a `memberIds: array<string>` field on the
classroom doc lets `ClassroomService.FetchMyClassrooms()` run a single
`array-contains` query instead of scanning every classroom. The
`members` subcollection still holds the per-student `joinedAt` timestamp
for display purposes. Both are written together, in the same batch, by
`ClassroomService.JoinClassroom()`.
