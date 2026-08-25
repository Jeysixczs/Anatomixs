using System;
using System.Collections;
using System.Collections.Generic;
using Firebase;
using Firebase.Firestore;
using Firebase.Messaging;
using Firebase.Extensions;
using UnityEngine;
using Anatomia3D.UI;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Handles Firebase Cloud Messaging setup, token retrieval, and incoming
    /// message handling for this device.
    ///
    /// Usage: drop this on the same persistent GameObject as UIManager (or any
    /// DontDestroyOnLoad object) and it will initialize itself in Awake().
    /// The registration token is logged once available - copy it from
    /// Logcat/the console and paste it into Firebase Console > Engage >
    /// Messaging > New Campaign > Target > Single device to send yourself a
    /// test push.
    /// </summary>
    public class NotificationManager : MonoBehaviour
    {
        public static NotificationManager Instance { get; private set; }

        /// <summary>The current FCM registration token for this device, once available.</summary>
        public string CurrentToken { get; private set; }

        /// <summary>Fired whenever a token is generated or refreshed.</summary>
        public event Action<string> OnTokenReceived;

        /// <summary>Fired when a push notification is received while the app is in the foreground.</summary>
        public event Action<MessageReceivedEventArgs> OnMessageReceivedInForeground;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else
            {
                Destroy(gameObject);
                return;
            }

            InitializeFirebase();
        }

        private void InitializeFirebase()
        {
            FirebaseApp.CheckAndFixDependenciesAsync().ContinueWithOnMainThread(task =>
            {
                var status = task.Result;
                if (status == DependencyStatus.Available)
                {
                    Debug.Log("[NotificationManager] Firebase dependencies resolved. Initializing messaging.");

                    FirebaseMessaging.TokenReceived += OnTokenReceivedHandler;
                    FirebaseMessaging.MessageReceived += OnMessageReceivedHandler;

                    // Explicitly request the current token in case TokenReceived
                    // already fired before we subscribed (can happen on some devices).
                    FirebaseMessaging.GetTokenAsync().ContinueWithOnMainThread(tokenTask =>
                    {
                        if (tokenTask.IsFaulted || tokenTask.IsCanceled)
                        {
                            Debug.LogError($"[NotificationManager] Failed to get FCM token: {tokenTask.Exception}");
                            return;
                        }

                        HandleNewToken(tokenTask.Result);
                    });
                }
                else
                {
                    Debug.LogError($"[NotificationManager] Could not resolve Firebase dependencies: {status}");
                }
            });
        }

        private void OnTokenReceivedHandler(object sender, TokenReceivedEventArgs token)
        {
            HandleNewToken(token.Token);
        }

        private void HandleNewToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return;

            CurrentToken = token;
            Debug.Log($"[NotificationManager] FCM Token: {token}");
            OnTokenReceived?.Invoke(token);

            // A fresh token can arrive before the student has signed in (e.g.
            // right at app launch), so PlayerSessionManager.CurrentStudent may
            // still be null here. Rather than requiring some other script to
            // remember to call a save method after login, just retry quietly
            // in the background until a student is signed in and the save
            // succeeds, then stop. Restarting this on every new token is fine -
            // a token refresh is rare, and StopCoroutine below cancels any
            // still-running attempt first so there's never more than one at a time.
            StopCoroutine(nameof(SaveTokenWhenReadyRoutine));
            StartCoroutine(SaveTokenWhenReadyRoutine());
        }

        private IEnumerator SaveTokenWhenReadyRoutine()
        {
            while (true)
            {
                var student = PlayerSessionManager.Instance?.CurrentStudent;
                bool firestoreReady = FirebaseBootstrap.Instance != null && FirebaseBootstrap.Instance.Db != null;

                if (student != null && firestoreReady)
                {
                    var studentRef = FirebaseBootstrap.Instance.Db.Collection("students").Document(student.Uid);
                    var task = studentRef.UpdateAsync(new Dictionary<string, object> { { "fcmToken", CurrentToken } });

                    yield return new WaitUntil(() => task.IsCompleted);

                    if (!task.IsFaulted && !task.IsCanceled)
                    {
                        Debug.Log("[NotificationManager] FCM token saved to Firestore.");
                        SubscribeToExistingClassroomTopics();
                        yield break; // done - no need to keep retrying
                    }

                    Debug.LogWarning($"[NotificationManager] Failed to save FCM token, will retry: {task.Exception?.Message}");
                }

                yield return new WaitForSeconds(3f);
            }
        }

        /// <summary>Re-subscribes this device to "classroom_{id}" topics for every
        /// classroom this student is already enrolled in. Covers students who
        /// joined a classroom before per-classroom topic subscription existed, or
        /// who reinstalled the app (a fresh install has no topic subscriptions
        /// even though Firestore still shows them as a member). New joins going
        /// forward subscribe immediately in ClassroomService.JoinClassroom() - this
        /// is just the catch-up path.</summary>
        private void SubscribeToExistingClassroomTopics()
        {
            if (ClassroomService.Instance == null) return;

            ClassroomService.Instance.FetchMyClassrooms(classrooms =>
            {
                foreach (var classroom in classrooms)
                {
                    FirebaseMessaging.SubscribeAsync($"classroom_{classroom.ClassroomId}")
                        .ContinueWithOnMainThread(task =>
                        {
                            if (task.IsFaulted || task.IsCanceled)
                            {
                                Debug.LogWarning($"[NotificationManager] Failed to subscribe to classroom_{classroom.ClassroomId}: {task.Exception}");
                            }
                        });
                }
            });
        }

        private void OnMessageReceivedHandler(object sender, MessageReceivedEventArgs e)
        {
            var notification = e.Message.Notification;
            var title = notification?.Title ?? "(no title)";
            var body = notification?.Body ?? "(no body)";

            Debug.Log($"[NotificationManager] Message received - Title: {title}, Body: {body}, " +
                      $"NotificationOpened: {e.Message.NotificationOpened}");

            // Log any custom data payload keys too, useful for routing to a
            // specific screen depending on payload content.
            foreach (var kvp in e.Message.Data)
            {
                Debug.Log($"[NotificationManager] Data payload - {kvp.Key}: {kvp.Value}");
            }

            // e.Message.NotificationOpened is true when this callback fired
            // because the user TAPPED a notification (app was backgrounded or
            // killed) rather than the message just arriving while the app was
            // already in the foreground. That's the case where we want to
            // jump straight to the relevant screen.
            if (e.Message.NotificationOpened)
            {
                Debug.Log("[NotificationManager] Notification tapped - navigating to Student Notifications screen.");
                UIManager.Instance?.ShowStudentNotifications();
            }

            OnMessageReceivedInForeground?.Invoke(e);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                FirebaseMessaging.TokenReceived -= OnTokenReceivedHandler;
                FirebaseMessaging.MessageReceived -= OnMessageReceivedHandler;
            }
        }
    }
}
