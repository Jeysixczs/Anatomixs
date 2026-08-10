using System;
using Firebase;
using Firebase.Auth;
using Firebase.Firestore;
using Firebase.Extensions;
using UnityEngine;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Initializes the Firebase App, Auth and Firestore instances once at
    /// startup and exposes them as singletons.
    ///
    /// Attach this to the same persistent Bootstrap GameObject as UIManager.
    /// It must finish before PlayerSessionManager / AdminAuthService /
    /// ClassroomService / AdminClassroomService touch Auth or Db - either
    /// give it an earlier Script Execution Order, or subscribe to
    /// OnReady / check IsReady before calling into those services.
    /// </summary>
    public class FirebaseBootstrap : MonoBehaviour
    {
        public static FirebaseBootstrap Instance { get; private set; }

        [Header("Google Sign-In")]
        [Tooltip("Firebase Console -> Authentication -> Sign-in method -> Google -> " +
                 "Web SDK configuration -> Web client ID. NOT the Android/iOS OAuth " +
                 "client ID - Firebase auto-creates this Web one when you enable Google " +
                 "as a sign-in provider, and it's what GoogleAuthProvider needs on both platforms.")]
        [SerializeField] private string googleWebClientId;
        public string GoogleWebClientId => googleWebClientId;

        public FirebaseAuth Auth { get; private set; }
        public FirebaseFirestore Db { get; private set; }
        public bool IsReady { get; private set; }

        /// <summary>Fires once, after Firebase finishes initializing.</summary>
        public event Action OnReady;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            FirebaseApp.CheckAndFixDependenciesAsync().ContinueWithOnMainThread(task =>
            {
                var status = task.Result;
                if (status == DependencyStatus.Available)
                {
                    var app = FirebaseApp.DefaultInstance;
                    Auth = FirebaseAuth.GetAuth(app);
                    Db = FirebaseFirestore.GetInstance(app);

                    // Offline persistence - keeps classroom/quiz screens usable
                    // on flaky school wifi and queues writes until reconnect.
                    // (Settings itself is read-only in this SDK version - mutate
                    // the field on the existing object instead of reassigning it.)
                    Db.Settings.PersistenceEnabled = true;

                    IsReady = true;
                    Debug.Log("[FirebaseBootstrap] Firebase ready.");
                    OnReady?.Invoke();
                }
                else
                {
                    Debug.LogError($"[FirebaseBootstrap] Could not resolve Firebase dependencies: {status}");
                }
            });
        }
    }
}