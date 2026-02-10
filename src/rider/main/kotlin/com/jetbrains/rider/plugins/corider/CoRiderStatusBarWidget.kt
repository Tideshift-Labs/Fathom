package com.jetbrains.rider.plugins.corider

import com.intellij.openapi.actionSystem.*
import com.intellij.openapi.application.PathManager
import com.intellij.openapi.application.invokeLater
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileTypes.PlainTextFileType
import com.intellij.openapi.options.ShowSettingsUtil
import com.intellij.openapi.project.DumbAwareAction
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.popup.JBPopupFactory
import com.intellij.openapi.ui.popup.ListPopup
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.wm.StatusBar
import com.intellij.openapi.wm.StatusBarWidget
import com.intellij.testFramework.LightVirtualFile
import com.intellij.ui.awt.RelativePoint
import com.jetbrains.rd.ide.model.CompanionPluginInfo
import com.jetbrains.rd.ide.model.CompanionPluginStatus
import com.jetbrains.rd.ide.model.coRiderModel
import com.jetbrains.rd.util.lifetime.LifetimeDefinition
import com.jetbrains.rider.projectView.solution
import java.awt.event.MouseEvent
import java.io.File
import javax.swing.Icon

@Suppress("DEPRECATION")
class CoRiderStatusBarWidget(private val project: Project) : StatusBarWidget, StatusBarWidget.IconPresentation {

    companion object {
        const val ID = "CoRiderStatusBarWidget"
    }

    private var statusBar: StatusBar? = null
    private val lifetimeDef = LifetimeDefinition()

    @Volatile
    private var currentInfo: CompanionPluginInfo? = null

    override fun ID(): String = ID

    override fun getPresentation(): StatusBarWidget.WidgetPresentation = this

    override fun install(statusBar: StatusBar) {
        this.statusBar = statusBar
        Disposer.register(project, this)

        val solution = project.solution
        val protocol = solution.protocol ?: return
        val model = solution.coRiderModel

        protocol.scheduler.queue {
            model.companionPluginStatus.advise(lifetimeDef.lifetime) { info ->
                currentInfo = info
                invokeLater {
                    this.statusBar?.updateWidget(ID)
                }
            }
        }
    }

    override fun dispose() {
        lifetimeDef.terminate()
        statusBar = null
    }

    override fun getIcon(): Icon {
        val info = currentInfo
        return if (info != null && (info.status == CompanionPluginStatus.NotInstalled || info.status == CompanionPluginStatus.Outdated)) {
            CoRiderIcons.StatusBarWarning
        } else {
            CoRiderIcons.StatusBarDefault
        }
    }

    override fun getTooltipText(): String {
        val info = currentInfo ?: return "CoRider"
        return when (info.status) {
            CompanionPluginStatus.NotInstalled -> "CoRider: UE companion plugin not installed"
            CompanionPluginStatus.Outdated -> "CoRider: UE companion plugin outdated (${info.installedVersion} -> ${info.bundledVersion})"
            CompanionPluginStatus.Installed -> "CoRider: UE companion plugin installed (needs build)"
            CompanionPluginStatus.UpToDate -> "CoRider: UE companion plugin up to date"
        }
    }

    override fun getClickConsumer(): com.intellij.util.Consumer<MouseEvent> {
        return com.intellij.util.Consumer { e ->
            val popup = buildPopup()
            val point = RelativePoint(e.component, e.point)
            popup.show(point)
        }
    }

    private fun buildPopup(): ListPopup {
        val group = DefaultActionGroup()
        val info = currentInfo

        // Status label (non-actionable)
        val statusText = when (info?.status) {
            CompanionPluginStatus.NotInstalled -> "UE Plugin: Not Installed"
            CompanionPluginStatus.Outdated -> "UE Plugin: Outdated (${info.installedVersion})"
            CompanionPluginStatus.Installed -> "UE Plugin: Installed (needs build)"
            CompanionPluginStatus.UpToDate -> "UE Plugin: Up to Date"
            null -> "UE Plugin: Unknown"
        }
        group.add(object : AnAction(statusText) {
            override fun actionPerformed(e: AnActionEvent) {}
            override fun update(e: AnActionEvent) {
                e.presentation.isEnabled = false
            }
            override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT
        })

        group.addSeparator()

        // Contextual action
        when (info?.status) {
            CompanionPluginStatus.NotInstalled, CompanionPluginStatus.Outdated -> {
                group.add(object : DumbAwareAction("Install Companion Plugin") {
                    override fun actionPerformed(e: AnActionEvent) {
                        val protocol = project.solution.protocol ?: return
                        protocol.scheduler.queue {
                            project.solution.coRiderModel.installCompanionPlugin.fire(Unit)
                        }
                    }
                    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT
                })
            }
            CompanionPluginStatus.Installed -> {
                group.add(object : DumbAwareAction("Build Companion Plugin") {
                    override fun actionPerformed(e: AnActionEvent) {
                        val protocol = project.solution.protocol ?: return
                        protocol.scheduler.queue {
                            project.solution.coRiderModel.buildCompanionPlugin.fire(Unit)
                        }
                    }
                    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT
                })
            }
            CompanionPluginStatus.UpToDate, null -> { /* no contextual action */ }
        }

        group.addSeparator()

        // Open Log File action
        group.add(object : DumbAwareAction("Open Log File") {
            override fun actionPerformed(e: AnActionEvent) {
                val logDir = File(PathManager.getLogPath())
                val logFiles = logDir.listFiles { f -> f.name.startsWith("backend.") && f.name.endsWith(".log") }
                val logFile = logFiles
                    ?.maxByOrNull { it.lastModified() }

                if (logFile == null || !logFile.exists()) {
                    com.intellij.openapi.ui.Messages.showInfoMessage(
                        project,
                        "No CoRider log entries found. The backend log file was not found.",
                        "CoRider Log"
                    )
                    return
                }

                val filteredLines = logFile.useLines { lines ->
                    lines.filter { it.contains("CoRider") }.toList()
                }

                if (filteredLines.isEmpty()) {
                    com.intellij.openapi.ui.Messages.showInfoMessage(
                        project,
                        "No CoRider log entries found in ${logFile.name}.",
                        "CoRider Log"
                    )
                    return
                }

                val content = filteredLines.joinToString("\n")
                val virtualFile = LightVirtualFile("CoRider Log", PlainTextFileType.INSTANCE, content)
                invokeLater {
                    FileEditorManager.getInstance(project).openFile(virtualFile, true)
                }
            }
            override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT
        })

        // Settings action
        group.add(object : DumbAwareAction("CoRider Settings...") {
            override fun actionPerformed(e: AnActionEvent) {
                ShowSettingsUtil.getInstance().showSettingsDialog(
                    project,
                    "com.jetbrains.rider.plugins.corider.CoRiderConfigurable"
                )
            }
            override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT
        })

        return JBPopupFactory.getInstance().createActionGroupPopup(
            "CoRider",
            group,
            DataContext.EMPTY_CONTEXT,
            JBPopupFactory.ActionSelectionAid.SPEEDSEARCH,
            false
        )
    }
}
