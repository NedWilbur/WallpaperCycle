using System;

namespace WallpaperCycle
{
    public static class BackgroundPolicy
    {
        public static bool UsesBlackBackground(Settings settings, DateTime localTime)
        {
            return UsesBlackBackground(settings, localTime, false);
        }

        public static bool UsesBlackBackground(Settings settings, DateTime localTime, bool suppressScheduledBlack)
        {
            if (settings.BlackBackground) return true;
            return !suppressScheduledBlack && IsInScheduledBlackWindow(settings, localTime);
        }

        public static bool IsInScheduledBlackWindow(Settings settings, DateTime localTime)
        {
            if (!settings.BlackBackgroundScheduleEnabled) return false;

            int hour = localTime.Hour;
            int start = settings.BlackBackgroundStartHour;
            int end = settings.BlackBackgroundEndHour;
            return start < end ? hour >= start && hour < end : hour >= start || hour < end;
        }

        public static DateTime? ScheduledBlackWindowEnd(Settings settings, DateTime localTime)
        {
            if (!IsInScheduledBlackWindow(settings, localTime)) return null;
            int end = settings.BlackBackgroundEndHour;
            DateTime endDate = settings.BlackBackgroundStartHour < end || localTime.Hour < end
                ? localTime.Date
                : localTime.Date.AddDays(1);
            return endDate.AddHours(end);
        }
    }
}
