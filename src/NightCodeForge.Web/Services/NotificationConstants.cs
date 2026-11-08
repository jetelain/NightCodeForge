namespace NightCodeForge.Web.Services;

/// <summary>
/// Domain constants and payload templates for Web Worker push and OS notifications.
/// </summary>
public static class NotificationConstants
{
    public const string PreferencesStorageKey = "ncf_notification_preferences_v1";
    public const string DefaultBaseBranch = "main";
    public const string DefaultIconPath = "/icon-192.png";
    public const int DefaultDeduplicatorCapacity = 200;

    public static class JsFunctions
    {
        public const string GetPermissionState = "NightCodeForge.notifications.getPermissionState";
        public const string RequestPermission = "NightCodeForge.notifications.requestPermission";
        public const string IsSupported = "NightCodeForge.notifications.isSupported";
        public const string IsDocumentVisible = "NightCodeForge.notifications.isDocumentVisible";
        public const string GetPreferences = "NightCodeForge.notifications.getPreferences";
        public const string SavePreferences = "NightCodeForge.notifications.savePreferences";
        public const string ShowNotification = "NightCodeForge.notifications.showNotification";
    }

    public static class Subjects
    {
        public const string QuestionInbox = "question_inbox";
        public const string ReviewRoom = "review_room";
        public const string TaskMerged = "task_merged";
    }

    public static class Tags
    {
        public static string Question(int taskId) => $"ncf-question-{taskId}";
        public static string Review(int taskId) => $"ncf-review-{taskId}";
        public static string Merged(int taskId) => $"ncf-merged-{taskId}";
        public const string Test = "ncf-test-notification";
    }

    public static class Titles
    {
        public static string Question(int taskId) => $"❓ Human Input Needed: Task #{taskId}";
        public static string Review(int taskId) => $"🔍 Ready for Review: Task #{taskId}";
        public static string Merged(int taskId) => $"🚀 Task Merged: Task #{taskId}";
        public const string Test = "🔔 NightCodeForge: Test Notification";
    }

    public static class Bodies
    {
        public static string Question(string taskTitle) => $"{taskTitle} — The agent has a clarifying question waiting for your answer.";
        public static string Review(string taskTitle) => $"{taskTitle} — All automated gates passed. Ready for code review and merge approval.";
        public static string Merged(string taskTitle, string baseBranch) => $"{taskTitle} — Successfully merged into {baseBranch}.";
        public const string Test = "Notifications are active and configured correctly.";
    }

    public static class Urls
    {
        public static string Question(int taskId) => $"/inbox?taskId={taskId}";
        public static string Review(int taskId) => $"/review/{taskId}";
        public static string Merged(int taskId) => $"/tasks/{taskId}";
        public const string Settings = "/settings";
    }

    public static class Targets
    {
        public const string Inbox = "inbox";
        public const string ReviewSection = "review";
        public const string InspectorSection = "inspector";
        public const string MonitoringSection = "monitoring";
        public static string Review(int taskId) => $"review/{taskId}";
        public static string Tasks(int taskId) => $"tasks/{taskId}";
        public static string Inspector(int taskId) => $"inspector/{taskId}";
        public static string Monitoring(int taskId) => $"monitoring/{taskId}";
    }

    public static class PermissionStates
    {
        public const string Granted = "granted";
        public const string Denied = "denied";
        public const string Default = "default";
        public const string Unsupported = "unsupported";
    }
}

