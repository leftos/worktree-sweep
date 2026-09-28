//! Dates in local time for the detail pane: `2026-09-25 14:02 (2d ago)`.

use anyhow::{Context, Result};
use tracing::warn;
use windows::Win32::Foundation::SYSTEMTIME;
use windows::Win32::System::Time::SystemTimeToTzSpecificLocalTime;

use crate::report::age;

const MINUTE: i64 = 60;
const DAY: i64 = 86_400;

/// `unix` as `YYYY-MM-DD HH:MM (<age> ago)` in a time zone `offset_minutes` east of UTC, its age measured from
/// `now_unix`.
#[must_use]
pub fn format_local(unix: i64, offset_minutes: i32, now_unix: i64) -> String {
    let local = unix + i64::from(offset_minutes) * MINUTE;
    let (year, month, day) = civil_from_days(local.div_euclid(DAY));
    let seconds = local.rem_euclid(DAY);
    format!(
        "{year:04}-{month:02}-{day:02} {:02}:{:02} ({} ago)",
        seconds / 3600,
        seconds % 3600 / MINUTE,
        age(unix, now_unix)
    )
}

/// How many minutes east of UTC local time was at `unix`, with the daylight saving time in force at that date; 0,
/// with a warning, when Windows cannot say.
#[must_use]
pub fn utc_offset_minutes(unix: i64) -> i32 {
    local_offset(unix).unwrap_or_else(|error| {
        warn!("cannot read the local time zone offset; showing UTC: {error:#}");
        0
    })
}

fn local_offset(unix: i64) -> Result<i32> {
    let utc = system_time(unix)?;
    let mut local = SYSTEMTIME::default();
    // SAFETY: both pointers point at live SYSTEMTIME values for the whole call; `None` means the current time zone.
    unsafe { SystemTimeToTzSpecificLocalTime(None, &raw const utc, &raw mut local) }
        .with_context(|| format!("SystemTimeToTzSpecificLocalTime failed for {unix}"))?;
    let local_minutes = days_from_civil(
        i64::from(local.wYear),
        i64::from(local.wMonth),
        i64::from(local.wDay),
    ) * (DAY / MINUTE)
        + i64::from(local.wHour) * 60
        + i64::from(local.wMinute);
    i32::try_from(local_minutes - unix.div_euclid(MINUTE))
        .with_context(|| format!("the local offset at {unix} is out of range"))
}

fn system_time(unix: i64) -> Result<SYSTEMTIME> {
    let days = unix.div_euclid(DAY);
    let seconds = unix.rem_euclid(DAY);
    let (year, month, day) = civil_from_days(days);
    let field = |value: i64| {
        u16::try_from(value).with_context(|| format!("{unix} is outside the Windows date range"))
    };
    Ok(SYSTEMTIME {
        wYear: field(year)?,
        wMonth: field(month)?,
        // 1970-01-01 was a Thursday; Sunday is 0.
        wDayOfWeek: field((days + 4).rem_euclid(7))?,
        wDay: field(day)?,
        wHour: field(seconds / 3600)?,
        wMinute: field(seconds % 3600 / MINUTE)?,
        wSecond: field(seconds % MINUTE)?,
        wMilliseconds: 0,
    })
}

/// The proleptic Gregorian `(year, month, day)` of the day `days` after 1970-01-01.
fn civil_from_days(days: i64) -> (i64, i64, i64) {
    let shifted = days + 719_468;
    let era = shifted.div_euclid(146_097);
    let day_of_era = shifted.rem_euclid(146_097);
    let year_of_era =
        (day_of_era - day_of_era / 1460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let month_from_march = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * month_from_march + 2) / 5 + 1;
    let month = if month_from_march < 10 {
        month_from_march + 3
    } else {
        month_from_march - 9
    };
    let year = year_of_era + era * 400;
    (if month <= 2 { year + 1 } else { year }, month, day)
}

/// The number of days from 1970-01-01 to the proleptic Gregorian date `year-month-day`.
fn days_from_civil(year: i64, month: i64, day: i64) -> i64 {
    let year = if month <= 2 { year - 1 } else { year };
    let era = year.div_euclid(400);
    let year_of_era = year.rem_euclid(400);
    let month_from_march = if month > 2 { month - 3 } else { month + 9 };
    let day_of_year = (153 * month_from_march + 2) / 5 + day - 1;
    let day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    era * 146_097 + day_of_era - 719_468
}

#[cfg(test)]
mod tests {
    use super::*;

    const NOW: i64 = 1_790_000_000;
    /// 2026-09-21 00:00 UTC, the day `NOW` falls on.
    const NOW_DAY: i64 = 1_789_948_800;

    #[test]
    fn format_local_utc_epoch_day() {
        assert_eq!(format_local(0, 0, 0), "1970-01-01 00:00 (0m ago)");
        assert_eq!(format_local(NOW, 0, NOW), "2026-09-21 14:13 (0m ago)");
    }

    #[test]
    fn format_local_applies_offset_across_midnight() {
        let late = NOW_DAY + 23 * 3600 + 30 * 60;
        assert_eq!(format_local(late, 120, late), "2026-09-22 01:30 (0m ago)");
        let early = NOW_DAY + 30 * 60;
        assert_eq!(format_local(early, -60, early), "2026-09-20 23:30 (0m ago)");
    }

    #[test]
    fn format_local_leap_day() {
        let leap_noon = 1_835_395_200 + 12 * 3600;
        assert_eq!(
            format_local(leap_noon, 0, leap_noon),
            "2028-02-29 12:00 (0m ago)"
        );
        assert_eq!(
            format_local(leap_noon + DAY, 0, leap_noon + DAY),
            "2028-03-01 12:00 (0m ago)"
        );
    }

    #[test]
    fn format_local_age_suffix() {
        assert!(format_local(NOW - 3 * DAY, 0, NOW).ends_with(" (3d ago)"));
        assert!(format_local(NOW - 2 * 3600, 0, NOW).ends_with(" (2h ago)"));
    }
}
