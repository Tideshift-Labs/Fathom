package com.tideshiftlabs.fathom

import com.intellij.ide.BrowserUtil
import com.intellij.openapi.actionSystem.*
import com.intellij.openapi.application.PathManager
import com.intellij.openapi.application.invokeLater
import com.intellij.openapi.components.service
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
import com.jetbrains.rd.ide.model.fathomModel
import com.jetbrains.rd.util.lifetime.LifetimeDefinition
import com.jetbrains.rider.projectView.solution
import java.awt.event.MouseEvent
import java.io.File
import javax.swing.Icon

@Suppress("DEPRECATION")
class FathomStatusBarWidget(private val project: Project) : StatusBarWidget, StatusBarWidget.IconPresentation {

    companion object {
        const val ID = "FathomStatusBarWidget"
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
        val model = solution.fathomModel

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
            FathomIcons.StatusBarWarning
        } else {
            FathomIcons.StatusBarDefault
        }
    }

    override fun getTooltipText(): String {
        val info = currentInfo ?: return "Fathom"
        return when (info.status) {
            CompanionPluginStatus.NotInstalled -> "Fathom: UE companion plugin not installed"
            CompanionPluginStatus.Outdated -> "Fathom: UE companion plugin outdated (${info.installedVersion} -> ${info.bundledVersion})"
            CompanionPluginStatus.Installed -> "Fathom: UE companion plugin installed (needs build)"
            CompanionPluginStatus.UpToDate -> "Fathom: UE companion plugin up to date"
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
                            project.solution.fathomModel.installCompanionPlugin.fire(Unit)
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
                            project.solution.fathomModel.buildCompanionPlugin.fire(Unit)
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
                        "No Fathom Log entries found. The backend log file was not found.",
                        "Fathom Log"
                    )
                    return
                }

                val filteredLines = logFile.useLines { lines ->
                    lines.filter { it.contains("Fathom") }.toList()
                }

                if (filteredLines.isEmpty()) {
                    com.intellij.openapi.ui.Messages.showInfoMessage(
                        project,
                        "No Fathom Log entries found in ${logFile.name}.",
                        "Fathom Log"
                    )
                    return
                }

                val content = filteredLines.joinToString("\n")
                val virtualFile = LightVirtualFile("Fathom Log", PlainTextFileType.INSTANCE, content)
                invokeLater {
                    FileEditorManager.getInstance(project).openFile(virtualFile, true)
                }
            }
            override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT
        })

        // Open in Browser action
        group.add(object : DumbAwareAction("Open in Browser") {
            override fun actionPerformed(e: AnActionEvent) {
                val port = service<FathomSettings>().state.port
                BrowserUtil.browse("http://localhost:$port/")
            }
            override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT
        })

        // Settings action
        group.add(object : DumbAwareAction("Fathom Settings...") {
            override fun actionPerformed(e: AnActionEvent) {
                ShowSettingsUtil.getInstance().showSettingsDialog(
                    project,
                    "com.tideshiftlabs.fathom.FathomConfigurable"
                )
            }
            override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT
        })

        return JBPopupFactory.getInstance().createActionGroupPopup(
            "Fathom",
            group,
            DataContext.EMPTY_CONTEXT,
            JBPopupFactory.ActionSelectionAid.SPEEDSEARCH,
            false
        )
    }
}
