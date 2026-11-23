// The Keystone widget for the Mac's desktop and Notification Centre: open tasks, bills to pay and the next
// appointments. Keystone writes a small summary next to its database ("widget.json"); this widget only reads it.
// Built into Keystone.app by scripts/package-mac.sh. Texts come from Keystone in its language; only dates are
// formatted here, so "Today" and "Overdue" stay right on later days.

import SwiftUI
import WidgetKit

// MARK: - The summary Keystone writes

struct Snapshot: Decodable {
    let version: Int
    let updatedAt: String
    let language: String
    let detailsHidden: Bool
    let labels: [String: String]
    let tasks: Section
    let bills: Section
    let appointments: Section

    func label(_ key: String) -> String { labels[key] ?? key }
}

struct Section: Decodable {
    let count: Int
    let overdue: Int
    let total: String?
    let items: [Item]
}

struct Item: Decodable {
    let title: String
    let detail: String?
    let date: String?
    let start: String?
    let allDay: Bool
}

enum SnapshotFile {
    /// The real home folder: inside its sandbox the widget's own home is a container elsewhere.
    static var url: URL {
        let home = getpwuid(getuid()).flatMap { String(validatingCString: $0.pointee.pw_dir) } ?? NSHomeDirectory()
        return URL(fileURLWithPath: home)
            .appendingPathComponent("Library/Application Support/Keystone/widget.json")
    }

    static func load() -> Snapshot? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(Snapshot.self, from: data)
    }
}

// MARK: - Dates

/// "2026-11-24" or "2026-11-24T09:00:00…": the first 10 or 19 characters, read as local time.
func parseDate(_ text: String?) -> Date? {
    guard let text else { return nil }
    let formatter = DateFormatter()
    formatter.locale = Locale(identifier: "en_US_POSIX")
    formatter.timeZone = .current
    if text.count >= 19 {
        formatter.dateFormat = "yyyy-MM-dd'T'HH:mm:ss"
        return formatter.date(from: String(text.prefix(19)))
    }
    formatter.dateFormat = "yyyy-MM-dd"
    return formatter.date(from: String(text.prefix(10)))
}

struct When {
    let text: String
    let isOverdue: Bool
    let isSoon: Bool
}

/// "Overdue", "Today", "Tomorrow" or "24 Nov", plus the time for timed appointments.
func when(_ item: Item, in snapshot: Snapshot, now: Date, isAppointment: Bool = false) -> When? {
    guard let day = parseDate(item.date) else { return nil }
    let calendar = Calendar.current
    let today = calendar.startOfDay(for: now)
    let start = calendar.startOfDay(for: day)
    let days = calendar.dateComponents([.day], from: today, to: start).day ?? 0

    let locale = Locale(identifier: snapshot.language == "de" ? "de_CH" : "en_GB")
    var text: String
    switch days {
    case ..<0 where !isAppointment: text = snapshot.label("overdue")
    case 0: text = snapshot.label("today")
    case 1: text = snapshot.label("tomorrow")
    default: text = day.formatted(.dateTime.day().month(.abbreviated).locale(locale))
    }
    if isAppointment {
        if item.allDay {
            text += " · " + snapshot.label("allDay")
        } else if let startTime = parseDate(item.start) {
            let time = DateFormatter()
            time.dateFormat = "HH:mm"
            text += " · " + time.string(from: startTime)
        }
    }
    return When(text: text, isOverdue: days < 0 && !isAppointment, isSoon: days <= 1)
}

// MARK: - Timeline

struct OverviewEntry: TimelineEntry {
    let date: Date
    let snapshot: Snapshot?
}

struct OverviewProvider: TimelineProvider {
    func placeholder(in context: Context) -> OverviewEntry { OverviewEntry(date: .now, snapshot: SampleData.snapshot) }

    func getSnapshot(in context: Context, completion: @escaping (OverviewEntry) -> Void) {
        completion(OverviewEntry(date: .now, snapshot: SnapshotFile.load() ?? (context.isPreview ? SampleData.snapshot : nil)))
    }

    /// Read again every 15 minutes, and at midnight so "Today" and "Overdue" move on.
    func getTimeline(in context: Context, completion: @escaping (Timeline<OverviewEntry>) -> Void) {
        let now = Date.now
        let snapshot = SnapshotFile.load()
        let midnight = Calendar.current.startOfDay(for: now.addingTimeInterval(86_400))
        let entries = [OverviewEntry(date: now, snapshot: snapshot), OverviewEntry(date: midnight, snapshot: snapshot)]
        completion(Timeline(entries: entries, policy: .after(now.addingTimeInterval(15 * 60))))
    }
}

// MARK: - Views

let brand = Color(red: 0.02, green: 0.59, blue: 0.41)

struct SectionHeader: View {
    let symbol: String
    let title: String
    let count: Int
    let extra: String?

    var body: some View {
        HStack(spacing: 5) {
            Image(systemName: symbol).foregroundStyle(brand).font(.caption.weight(.semibold))
            Text(title).font(.caption.weight(.semibold)).lineLimit(1)
            Spacer(minLength: 2)
            Text("\(count)").font(.caption.weight(.bold)).monospacedDigit()
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(title): \(count)\(extra.map { ", \($0)" } ?? "")")
    }
}

struct ItemRow: View {
    let item: Item
    let snapshot: Snapshot
    let now: Date
    var isAppointment = false
    var showDetail = true
    /// Narrow columns (the medium widget): the date goes under the title.
    var twoLines = false

    /// One line: the title on the left; when (and for bills the amount) on the right, red when overdue.
    var body: some View {
        let moment = when(item, in: snapshot, now: now, isAppointment: isAppointment)
        if twoLines {
            VStack(alignment: .leading, spacing: 0) {
                Text(item.title).font(.caption).lineLimit(1)
                if let moment {
                    Text(moment.text).font(.caption2).lineLimit(1)
                        .foregroundStyle(moment.isOverdue ? Color.red : moment.isSoon ? Color.orange : Color.secondary)
                }
            }
        } else {
            oneLine(moment)
        }
    }

    func oneLine(_ moment: When?) -> some View {
        HStack(spacing: 6) {
            Text(item.title).font(.caption).lineLimit(1).truncationMode(.tail)
            Spacer(minLength: 4)
            if showDetail, let detail = item.detail, !isAppointment {
                Text(detail).font(.caption2.weight(.medium)).monospacedDigit().lineLimit(1)
            }
            if let moment {
                Text(moment.text)
                    .font(.caption2)
                    .foregroundStyle(moment.isOverdue ? Color.red : moment.isSoon ? Color.orange : Color.secondary)
                    .lineLimit(1)
                    .fixedSize()
            }
        }
    }
}

struct SectionView: View {
    let symbol: String
    let titleKey: String
    let emptyKey: String
    let section: Section
    let snapshot: Snapshot
    let now: Date
    let maxItems: Int
    var isAppointment = false
    var showDetail = true
    var twoLines = false

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            SectionHeader(symbol: symbol, title: snapshot.label(titleKey), count: section.count, extra: section.total)
            if let total = section.total, section.count > 0 {
                Text(total).font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
            }
            if section.count == 0 {
                Text(snapshot.label(emptyKey)).font(.caption2).foregroundStyle(.secondary)
            } else if snapshot.detailsHidden {
                if section.overdue > 0 {
                    Text("\(section.overdue) \(snapshot.label("overdue"))").font(.caption2).foregroundStyle(.red)
                }
            } else {
                ForEach(Array(section.items.prefix(maxItems).enumerated()), id: \.offset) { _, item in
                    ItemRow(item: item, snapshot: snapshot, now: now, isAppointment: isAppointment, showDetail: showDetail, twoLines: twoLines)
                }
                if section.count > maxItems {
                    Text(snapshot.label("more").replacingOccurrences(of: "{0}", with: "\(section.count - maxItems)"))
                        .font(.caption2).foregroundStyle(.secondary)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// One line per kind for the small widget: the count, and the most pressing item.
struct SmallLine: View {
    let symbol: String
    let title: String
    let count: Int
    let note: String?
    let noteIsUrgent: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 5) {
                Image(systemName: symbol).foregroundStyle(brand).font(.caption.weight(.semibold)).frame(width: 14)
                Text(title).font(.caption).lineLimit(1)
                Spacer(minLength: 2)
                Text("\(count)").font(.callout.weight(.bold)).monospacedDigit()
            }
            if let note {
                Text(note).font(.caption2).foregroundStyle(noteIsUrgent ? Color.red : Color.secondary).lineLimit(1)
                    .padding(.leading, 19)
            }
        }
    }
}

struct OverviewView: View {
    let entry: OverviewEntry
    let family: WidgetFamily

    var body: some View {
        if let snapshot = entry.snapshot {
            content(snapshot)
        } else {
            VStack(alignment: .leading, spacing: 6) {
                Text("Keystone").font(.headline)
                Text("Open Keystone once to show your overview here.").font(.caption).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
    }

    @ViewBuilder
    func content(_ s: Snapshot) -> some View {
        let now = entry.date
        switch family {
        case .systemSmall:
            VStack(alignment: .leading, spacing: 7) {
                SmallLine(symbol: "checklist", title: s.label("tasks"), count: s.tasks.count,
                          note: s.tasks.overdue > 0 ? "\(s.tasks.overdue) \(s.label("overdue"))" : nil, noteIsUrgent: true)
                SmallLine(symbol: "doc.text", title: s.label("bills"), count: s.bills.count,
                          note: s.bills.count > 0 ? s.bills.total : nil, noteIsUrgent: s.bills.overdue > 0)
                SmallLine(symbol: "calendar", title: s.label("appointments"), count: s.appointments.count,
                          note: s.appointments.items.first.flatMap { item in
                              let moment = when(item, in: s, now: now, isAppointment: true)?.text ?? ""
                              return s.detailsHidden ? moment : "\(item.title) · \(moment)"
                          },
                          noteIsUrgent: false)
                Spacer(minLength: 0)
            }
        case .systemMedium:
            HStack(alignment: .top, spacing: 12) {
                SectionView(symbol: "checklist", titleKey: "tasks", emptyKey: "noTasks", section: s.tasks, snapshot: s, now: now,
                            maxItems: 2, twoLines: true)
                SectionView(symbol: "doc.text", titleKey: "billsShort", emptyKey: "noBills", section: s.bills, snapshot: s, now: now,
                            maxItems: 2, showDetail: false, twoLines: true)
                SectionView(symbol: "calendar", titleKey: "appointmentsShort", emptyKey: "noAppointments", section: s.appointments, snapshot: s,
                            now: now, maxItems: 2, isAppointment: true, twoLines: true)
            }
            .frame(maxHeight: .infinity, alignment: .top)
        default:
            VStack(alignment: .leading, spacing: 12) {
                SectionView(symbol: "checklist", titleKey: "tasks", emptyKey: "noTasks", section: s.tasks, snapshot: s, now: now, maxItems: 4)
                Divider()
                SectionView(symbol: "doc.text", titleKey: "bills", emptyKey: "noBills", section: s.bills, snapshot: s, now: now, maxItems: 4)
                Divider()
                SectionView(symbol: "calendar", titleKey: "appointments", emptyKey: "noAppointments", section: s.appointments, snapshot: s,
                            now: now, maxItems: 4, isAppointment: true)
                if s.detailsHidden {
                    Text(s.label("hidden")).font(.caption2).foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
            }
        }
    }
}

struct OverviewEntryView: View {
    @Environment(\.widgetFamily) private var family
    let entry: OverviewEntry

    var body: some View {
        OverviewView(entry: entry, family: family)
            .containerBackground(.fill.tertiary, for: .widget)
    }
}

// MARK: - Widget

struct OverviewWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "KeystoneOverview", provider: OverviewProvider()) { entry in
            OverviewEntryView(entry: entry)
        }
        .configurationDisplayName("Keystone")
        .description("Open tasks, bills to pay and the next appointments.")
        .supportedFamilies([.systemSmall, .systemMedium, .systemLarge])
    }
}

@main
struct KeystoneWidgets: WidgetBundle {
    var body: some Widget {
        OverviewWidget()
    }
}

// MARK: - Shown in the widget gallery before Keystone has written its summary (made-up household)

enum SampleData {
    static let snapshot: Snapshot = {
        let json = """
        {"version":1,"updatedAt":"2026-11-23T09:00:00","language":"en","detailsHidden":false,
         "labels":{"tasks":"Tasks","bills":"Bills to pay","appointments":"Appointments","noTasks":"Nothing to do",
                   "noBills":"No bills to pay","noAppointments":"Nothing planned","today":"Today","tomorrow":"Tomorrow",
                   "overdue":"Overdue","allDay":"All day","more":"+{0} more"},
         "tasks":{"count":3,"overdue":1,"items":[{"title":"Renew passport","date":"2026-11-20","allDay":false},
                  {"title":"Send tax return","date":"2026-11-30","allDay":false}]},
         "bills":{"count":2,"overdue":0,"total":"CHF 1'270","items":[{"title":"Helsana","detail":"CHF 1'080","date":"2026-12-01","allDay":false}]},
         "appointments":{"count":1,"overdue":0,"items":[{"title":"Dentist check-up","date":"2026-11-24","start":"2026-11-24T09:00:00","allDay":false}]}}
        """
        return try! JSONDecoder().decode(Snapshot.self, from: Data(json.utf8))
    }()
}
