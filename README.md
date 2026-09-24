# Anatomia

[![Unity](https://img.shields.io/badge/Unity-6000.3.8f1-black?logo=unity)](https://unity.com/)
[![C%23](https://img.shields.io/badge/C%23-Programming_Language-239120?logo=csharp)](https://learn.microsoft.com/en-us/dotnet/csharp/)
[![Android](https://img.shields.io/badge/Platform-Android-3DDC84?logo=android)](https://www.android.com/)
[![Firebase](https://img.shields.io/badge/Backend-Firebase-FFCA28?logo=firebase)](https://firebase.google.com/)
[![GitHub](https://img.shields.io/badge/Repository-GitHub-181717?logo=github)](https://github.com/Jeysixczs/Anatomixs)

Anatomia is a gamified mobile application designed to support interactive human anatomy learning for Radiologic Technology (RadTech) students. It combines interactive 3D anatomy models, game-based identification activities, quizzes, classroom learning, gamification, progress tracking, and Firebase-powered services.

## Overview

The application provides students with an interactive environment for exploring human anatomical structures and practicing anatomy identification.

The current 3D anatomy systems are:

- Skeletal System
- Muscular System
- Cardiovascular System

Anatomia is designed as an online-first application with selected offline-supported learning and authentication functionality.

## Features

### Interactive 3D Anatomy

- Skeletal, Muscular, and Cardiovascular systems
- Searchable anatomical structures
- Touch-based structure selection
- One-finger model rotation
- Two-finger pinch zoom
- Two-finger pan
- Camera focus and structure zoom
- Structure isolation
- Structure hiding
- Restore and undo actions
- Reset anatomy model
- Detailed anatomy information panel
- Draggable and minimizable information panel
- Audio guide and text-to-speech support
- Structure highlighting using a custom outline system

### Anatomy Play Mode

Anatomy Play Mode turns anatomy identification into a game-based learning activity.

- Structure identification questions
- Letter-based answer fields
- Anatomy structure guessing
- Hint system
- Character/letter reveal
- Attempt tracking
- Points
- Streaks
- Progress tracking
- Answered structure tracking
- Isolate Answered functionality
- Local progress storage
- Firebase synchronization
- System-specific Play Mode progress

### Gamification

- Points
- Levels
- Streaks
- Badges
- Achievements
- Learning progress
- Quiz rewards
- Performance tracking
- Weekly activity

### Quiz and Assessment System

The quiz system supports multiple assessment formats:

- Multiple Choice
- True/False
- Identification
- Enumeration
- Multiple Identification
- Image-Based Questions

Quiz functionality includes:

- Easy, Medium, and Hard difficulty
- Configurable question point values
- Weighted scoring
- Attempt limits
- Classroom-specific attempts
- Quiz deadlines
- Quiz history
- Score recording
- Best-score/progress handling
- Quiz completion tracking
- Student performance tracking
- Teacher-created quizzes

### Classroom System

Students can participate in teacher-managed classrooms.

- Join classrooms using classroom codes
- View enrolled classrooms
- View classroom details
- View classmates
- View classroom announcements
- View available quizzes
- View quiz results
- View classroom activity
- Receive classroom notifications
- Support classroom archiving

Teachers and administrators can:

- Create classrooms
- Manage classroom members
- Archive classrooms
- Create and manage quizzes
- Publish quizzes
- Monitor student performance
- Review classroom activity

### Student Dashboard

The student dashboard provides an overview of learning activity.

- Student profile
- Profile avatar
- Points
- Levels
- Badges
- Achievements
- Recent activity
- Quiz activity
- Classroom activity
- Anatomy progress
- Weekly activity
- Notifications
- Navigation to learning features

### Student Progress and Analytics

Students can review:

- Anatomy learning progress
- Quiz performance
- Score history
- Weekly activity
- Recent activities
- Badges and achievements
- Gamification progress

Administrators can review:

- Student performance
- Quiz results
- Completion rates
- Classroom statistics
- Performance trends
- Common mistakes
- Analytics
- Reports

### Admin and Teacher Functions

The administrative interface provides tools for managing learning activities.

- Admin dashboard
- Classroom management
- Quiz management
- Student management
- Gamification settings
- Analytics and reports
- Profile management
- Classroom performance monitoring
- Quiz completion monitoring
- Report export

### Authentication

Anatomia supports separate student and administrator authentication.

Student functions include:

- Account registration
- Login
- Password recovery
- Email verification
- Profile management
- Avatar management
- Logout
- Offline authentication for previously authenticated users
- Biometric/device authentication where supported

Admin functions include:

- Account registration
- Login
- Password recovery
- Profile management
- Account administration
- Logout

### Offline Support

Selected application functionality remains available when the device is offline after the user has previously authenticated.

Offline-supported functionality includes:

- Previously authenticated student access
- Local Play Mode progress
- Local anatomy learning data
- Offline authentication
- Synchronization when connectivity is restored

Anatomia is not completely offline. Features that require fresh cloud data, classroom information, quizzes, or Firebase communication require an internet connection.

### Firebase Integration

Anatomia uses Firebase for cloud-based application services.

- Firebase Authentication
- Cloud Firestore
- Firebase Cloud Messaging
- User data synchronization
- Classroom synchronization
- Quiz synchronization
- Progress synchronization
- Push notifications
- Classroom announcements

### Notifications

Firebase Cloud Messaging is used for classroom-related notifications.

Notification functionality includes:

- Notification token handling
- Classroom topic subscriptions
- Classroom topic unsubscriptions
- Subscription synchronization
- Incoming message handling
- Android notification channels
- Notification permission handling
- Navigation to related classroom content

## Technology Stack

| Technology | Purpose |
|---|---|
| Unity 6000.3.8f1 | Mobile application and 3D engine |
| C# | Application and gameplay logic |
| Unity UI Toolkit | User interface |
| UXML | UI structure |
| USS | UI styling |
| Firebase Authentication | User authentication |
| Cloud Firestore | Cloud database |
| Firebase Cloud Messaging | Push notifications |
| Android | Target mobile platform |
| JSON | Anatomy and assessment data |
| Google Sign-In | Google authentication support |
| Native Gallery | Device media interaction |
| Native File Picker | File selection |
| Cloudinary | Profile avatar storage |

## Project Structure

```text
Assets/
├── Model 1.0/
│   └── Descriptions Database/
│       ├── SkeletalDatabase.json
│       ├── MuscularDatabase.json
│       └── CardiovascularDatabase.json
│
├── UI/
│   ├── Backend/
│   │   ├── FirebaseBootstrap.cs
│   │   ├── PlayerSessionManager.cs
│   │   ├── ClassroomService.cs
│   │   ├── QuizService.cs
│   │   ├── BoneDatabaseService.cs
│   │   ├── FCMNotificationService.cs
│   │   ├── AnatomyPlayModeFirebase.cs
│   │   ├── AnatomyPlayModeLocalStorage.cs
│   │   └── AnatomyPlayModeSyncService.cs
│   │
│   ├── StudentDashboard/
│   ├── StudentExplore3d/
│   ├── StudentAnatomyScreen/
│   ├── StudentClassroom/
│   ├── StudentClassroomDetail/
│   ├── StudentClassroomHub/
│   ├── StudentAchievements/
│   ├── StudentProgress/
│   ├── StudentNotifications/
│   ├── StudentQuizSelection/
│   ├── StudentQuizScreen/
│   ├── StudentQuizResult/
│   ├── StudentLogin/
│   ├── StudentCreateAccount/
│   ├── StudentEditProfile/
│   ├── StudentProfile/
│   │
│   ├── AdminDashboard/
│   ├── AdminQuizManagement/
│   ├── AdminClassroomDetail/
│   ├── AdminClassroomCreated/
│   ├── AdminCreateClassroom/
│   ├── AdminCreateAccount/
│   ├── AdminGamificationSettings/
│   ├── AdminAnalyticsReports/
│   ├── AdminLogin/
│   └── AdminProfile/
│
├── Plugins/
│   ├── NativeFilePicker/
│   └── NativeGallery/
│
├── GoogleSignIn/
├── Firebase/
└── Resources/
```

## Main Application Screens

### Student

- Student Login
- Student Create Account
- Student Forgot Password
- Student Dashboard
- Student Profile
- Student Edit Profile
- Student Explore 3D
- Student Anatomy Screen
- Student Classroom
- Student Classroom Detail
- Student Classroom Hub
- Student Notifications
- Student Progress
- Student Achievements
- Student Quiz Selection
- Student Quiz Screen
- Student Quiz Result
- About Anatomia

### Admin

- Admin Login
- Admin Create Account
- Admin Forgot Password
- Admin Dashboard
- Admin Profile
- Admin Edit Profile
- Admin Create Classroom
- Admin Classroom Created
- Admin Classroom Detail
- Admin Quiz Management
- Admin Analytics and Reports
- Admin Gamification Settings
- About Anatomia Admin

## Important Controllers and Services

| Component | Responsibility |
|---|---|
| `UIManager` | Central application navigation and screen management |
| `AnatomyScreenController` | Interactive 3D anatomy experience |
| `AnatomyPlayModeController` | Anatomy identification gameplay |
| `AnatomyPlayModeFirebase` | Play Mode Firebase persistence |
| `AnatomyPlayModeLocalStorage` | Local Play Mode data |
| `AnatomyPlayModeSyncService` | Local and cloud synchronization |
| `QuizService` | Quiz creation, management, attempts, scoring, and results |
| `ClassroomService` | Student classroom functionality |
| `AdminClassroomService` | Administrative classroom management |
| `BoneDatabaseService` | Anatomy structure database access |
| `PlayerSessionManager` | Student session and profile management |
| `FirebaseBootstrap` | Firebase initialization |
| `FCMNotificationService` | Firebase Cloud Messaging |
| `AdminGamificationService` | Gamification administration |
| `AdminReportExportService` | Report generation and export |
| `NetworkStatusMonitor` | Network connectivity monitoring |
| `OfflineOverlay` | Offline interface state |

## Anatomy Data

Anatomy structure information is stored in JSON databases.

```text
Assets/Model 1.0/Descriptions Database/
```

Available databases:

```text
SkeletalDatabase.json
MuscularDatabase.json
CardiovascularDatabase.json
```

Each structure can contain:

- Structure key
- Display name
- Base name
- Description
- Reference/source information

The structure key is used to associate the 3D model with its corresponding anatomy information and gameplay data.

## Assessment Data

Pretest and posttest questions are stored in:

```text
Assets/UI/Resources/PretestPosttestQuestions.json
```

The assessment database supports anatomy-focused questions, including image-based questions.

## Application Architecture

```text
┌───────────────────────────────┐
│          UI Layer             │
│                               │
│ Student Controllers           │
│ Admin Controllers             │
│ UIManager                     │
└───────────────┬───────────────┘
                │
                ▼
┌───────────────────────────────┐
│        Service Layer          │
│                               │
│ Authentication               │
│ Classroom                    │
│ Quiz                         │
│ Anatomy Database             │
│ Gamification                 │
│ Notifications                │
│ Synchronization              │
└───────────────┬───────────────┘
                │
        ┌───────┴────────┐
        ▼                ▼
┌──────────────┐  ┌──────────────┐
│ Local Storage │  │   Firebase   │
│              │  │              │
│ Offline Data │  │ Auth         │
│ Play Mode    │  │ Firestore    │
│ Progress     │  │ FCM          │
└──────────────┘  └──────────────┘
```

The project separates user-interface controllers from backend and synchronization services to make the application easier to maintain and extend.

## Getting Started

### Requirements

Install the following before opening the project:

1. Unity Hub
2. Unity `6000.3.8f1`
3. Android Build Support
4. Android SDK, NDK, and OpenJDK components required by Unity
5. A Firebase project for cloud-backed functionality

### Clone the Repository

```bash
git clone https://github.com/Jeysixczs/Anatomia.git
cd Anatomia
```

Then open the project using Unity Hub with:

```text
Unity 6000.3.8f1
```

### Firebase Configuration

Firebase configuration is required for cloud-backed features.

Configure the Android application in your Firebase project and provide the appropriate Firebase configuration files required by the Unity Firebase SDK.

Do not commit private credentials, service-account keys, or other sensitive secrets to the repository.

### Android Build

Open the project in Unity and select the Android build target.

Configure:

- Package identifier
- Android SDK settings
- Application permissions
- Firebase configuration
- Application signing settings

Then build and deploy the application to an Android device.

## Application Flow

```text
Launch Application
        │
        ▼
Authentication
        │
        ├───────────────┐
        ▼               ▼
     Student          Admin
        │               │
        ▼               ▼
   Dashboard         Dashboard
        │               │
   ┌────┼────┐      ┌───┼────────┐
   ▼    ▼    ▼      ▼   ▼        ▼
  3D  Class  Quiz  Class Quiz  Analytics
   │    │    │      │   │        │
   └────┴────┘      └───┴────────┘
        │
        ▼
Gamification and Progress
        │
        ▼
Local Storage / Firebase
```

## Security

When deploying Anatomia:

- Use Firebase Security Rules to protect application data.
- Validate classroom membership and administrative permissions.
- Do not expose service-account private keys in the Unity project.
- Do not rely only on client-side validation for sensitive operations.
- Protect administrative operations with appropriate authorization.
- Keep third-party service credentials secure.

## Development Status

Anatomia is released as version `v1.0.0`.

This release contains the completed core functionality for:

- Interactive 3D anatomy
- Skeletal, Muscular, and Cardiovascular systems
- Anatomy identification gameplay
- Gamification
- Student accounts
- Admin accounts
- Classrooms
- Quizzes
- Assessments
- Progress tracking
- Analytics
- Notifications
- Offline-supported authentication
- Offline-supported learning functionality
- Firebase synchronization

## Intended Users

### Radiologic Technology Students

The primary users of Anatomia are Radiologic Technology students who need to learn and practice identifying human anatomical structures.

### Teachers and Administrators

Teachers and administrators can use the classroom, quiz, assessment, gamification, analytics, and reporting features to support anatomy instruction.

## Educational Purpose

Anatomia is an educational tool intended to supplement anatomy instruction.

It does not replace formal classroom instruction, laboratory activities, textbooks, medical references, or professional healthcare education.

## Repository

This repository contains the source code and project assets for Anatomia.

```text
Project: Anatomia
Version: v0.1.0
Platform: Android
Engine: Unity 6000.3.8f1
Backend: Firebase
Target: Radiologic Technology Students
```

## Developer

**FourAs**

GitHub: [Jeysixczs](https://github.com/Jeysixczs)

GitHub: [JMAcopio](https://github.com/JMacopio)

## License

Copyright © 2026 JeysiDev / Anatomia.

All rights reserved unless otherwise specified by the project owners or institution.

---

Anatomia — Gamified Mobile Application for Interactive Human Anatomy Learning.
