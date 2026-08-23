# Anatomia

A Unity-based educational application for learning human anatomy through interactive 3D exploration, play mode, and quizzes.

## Features
- Student and admin authentication flows
- Interactive anatomy modules (Skeletal, Muscular, Cardiovascular)
- Explore mode and Play mode for anatomy interaction
- Quiz gameplay, scoring, and results screens
- Admin quiz and classroom management
- Gamification settings and progress tracking
- Offline/local answer storage with sync support when online

## Requirements
- Unity Editor `6000.3.8f1`
- Unity packages from `/home/runner/work/Anatomixs/Anatomixs/Packages/manifest.json`, including:
  - `com.unity.inputsystem`
  - `com.unity.render-pipelines.universal`
  - `com.unity.ugui`
  - `com.unity.ai.navigation`
  - `com.unity.test-framework`
- Firebase project configuration (`google-services.json`)

## Installation
1. Clone the repository.
2. Open `/home/runner/work/Anatomixs/Anatomixs` in Unity Hub.
3. Use Unity Editor version `6000.3.8f1`.
4. Let Unity resolve packages automatically from `Packages/manifest.json`.
5. Ensure Firebase config files are present in `Assets/`.
6. Open `Assets/Scenes/AnatomiaScene.unity`.
7. Run the application in Play mode.

## Project Structure
- `/home/runner/work/Anatomixs/Anatomixs/Assets/` - Main Unity assets and source code
  - `UI/` - UI Toolkit screens (`.uxml`, `.uss`) and controllers (`.cs`)
  - `UI/Backend/` - Firebase and Firestore service layer (`PlayerSessionManager`, `QuizService`, `ClassroomService`, etc.)
  - `Scenes/` - Unity scenes (main scene: `AnatomiaScene.unity`)
  - `Model 1.0/Descriptions Database/` - Anatomy JSON databases (Skeletal, Muscular, Cardiovascular)
- `/home/runner/work/Anatomixs/Anatomixs/Packages/` - Unity package configuration (`manifest.json`, `packages-lock.json`)
- `/home/runner/work/Anatomixs/Anatomixs/ProjectSettings/` - Unity project settings and editor version

## Authors
- Jeysixczs
