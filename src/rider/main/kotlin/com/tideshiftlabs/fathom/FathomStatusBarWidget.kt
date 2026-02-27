package com.tideshiftlabs.fathom

import com.intellij.ide.BrowserUtil
import com.intellij.notification.Notification
import com.intellij.notification.NotificationAction
import com.intellij.notification.NotificationType
import com.intellij.notification.Notifications
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
        private const val NOTIFICATION_GROUP_ID = "Fathom.CompanionPlugin"
    }

    private var statusBar: StatusBar? = null
    private val lifetimeDef = LifetimeDefinition()

    @Volatile
    private var currentInfo: CompanionPluginInfo? = null

    @Volatile
    private var actionInProgress = false

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
                actionInProgress = false
                invokeLater {
                    this.statusBar?.updateWidget(ID)
                    showCompanionNotification(info, protocol, model)
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
            CompanionPluginStatus.UpToDate -> "Fathom: UE companion plugin up to date (${info.installLocation})"
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
            CompanionPluginStatus.Outdated -> when (info.installLocation) {
                "Engine" -> "UE Plugin: Outdated (Engine)"
                "Game" -> "UE Plugin: Outdated (Game)"
                "Both" -> "UE Plugin: Outdated (Engine + Game)"
                else -> "UE Plugin: Outdated (${info.installedVersion})"
            }
            CompanionPluginStatus.Installed -> "UE Plugin: Installed (needs build)"
            CompanionPluginStatus.UpToDate -> when (info.installLocation) {
                "Engine" -> "UE Plugin: Installed to Engine"
                "Game" -> "UE Plugin: Installed to Game"
                "Both" -> "UE Plugin: Installed to Engine + Game"
                else -> "UE Plugin: Up to Date"
            }
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

        // Contextual actions based on status and install location
        val busy = actionInProgress
        when (info?.status) {
            CompanionPluginStatus.NotInstalled -> {
                group.add(createInstallAction("Install to Engine", "Engine", busy))
                group.add(createInstallAction("Install to Game", "Game", busy))
            }
            CompanionPluginStatus.Outdated -> {
                when (info.installLocation) {
                    "Engine" -> group.add(createInstallAction("Update Engine Plugin", "Engine", busy))
                    "Game" -> {
                        group.add(createInstallAction("Update Game Plugin", "Game", busy))
                        group.add(createInstallAction("Install to Engine", "Engine", busy))
                    }
                    "Both" -> {
                        group.add(createInstallAction("Update Engine Plugin", "Engine", busy))
                        group.add(createInstallAction("Update Game Plugin", "Game", busy))
                    }
                    else -> group.add(createInstallAction("Install to Engine", "Engine", busy))
                }
            }
            CompanionPluginStatus.Installed -> {
                group.add(createBuildAction(busy))
            }
            CompanionPluginStatus.UpToDate -> {
                // If only installed to Game, offer Engine installation
                if (info.installLocation == "Game") {
                    group.add(createInstallAction("Install to Engine", "Engine", busy))
                }
            }
            null -> { /* no contextual action */ }
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
                    lines.filter { it.contains("Fathom") || it.contains("CompanionPlugin") }.toList()
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

    private fun createInstallAction(label: String, location: String, disabled: Boolean): DumbAwareAction {
        val text = if (disabled) "$label (in progress...)" else label
        return object : DumbAwareAction(text) {
            override fun actionPerformed(e: AnActionEvent) {
                actionInProgress = true
                val protocol = project.solution.protocol ?: return
                protocol.scheduler.queue {
                    project.solution.fathomModel.installCompanionPlugin.fire(location)
                }
            }
            override fun update(e: AnActionEvent) {
                e.presentation.isEnabled = !disabled
            }
            override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT
        }
    }

    private fun createBuildAction(disabled: Boolean): DumbAwareAction {
        val text = if (disabled) "Build Companion Plugin (in progress...)" else "Build Companion Plugin"
        return object : DumbAwareAction(text) {
            override fun actionPerformed(e: AnActionEvent) {
                actionInProgress = true
                val protocol = project.solution.protocol ?: return
                protocol.scheduler.queue {
                    project.solution.fathomModel.buildCompanionPlugin.fire(Unit)
                }
            }
            override fun update(e: AnActionEvent) {
                e.presentation.isEnabled = !disabled
            }
            override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT
        }
    }

    private fun showCompanionNotification(
        info: CompanionPluginInfo,
        protocol: com.jetbrains.rd.framework.IProtocol,
        model: com.jetbrains.rd.ide.model.FathomModel
    ) {
        val notification = when (info.status) {
            CompanionPluginStatus.NotInstalled -> {
                Notification(NOTIFICATION_GROUP_ID, "Fathom UE plugin not installed", info.message, NotificationType.WARNING).apply {
                    isImportant = true
                    addAction(NotificationAction.createSimple("Install to Engine") {
                        expire()
                        protocol.scheduler.queue { model.installCompanionPlugin.fire("Engine") }
                    })
                    addAction(NotificationAction.createSimple("Install to Game") {
                        expire()
                        protocol.scheduler.queue { model.installCompanionPlugin.fire("Game") }
                    })
                }
            }
            CompanionPluginStatus.Outdated -> {
                Notification(NOTIFICATION_GROUP_ID, "Fathom UE plugin outdated", info.message, NotificationType.WARNING).apply {
                    isImportant = true
                    when (info.installLocation) {
                        "Engine" -> {
                            addAction(NotificationAction.createSimple("Update") {
                                expire()
                                protocol.scheduler.queue { model.installCompanionPlugin.fire("Engine") }
                            })
                        }
                        "Game" -> {
                            addAction(NotificationAction.createSimple("Update") {
                                expire()
                                protocol.scheduler.queue { model.installCompanionPlugin.fire("Game") }
                            })
                            addAction(NotificationAction.createSimple("Install to Engine") {
                                expire()
                                protocol.scheduler.queue { model.installCompanionPlugin.fire("Engine") }
                            })
                        }
                        "Both" -> {
                            addAction(NotificationAction.createSimple("Update Engine") {
                                expire()
                                protocol.scheduler.queue { model.installCompanionPlugin.fire("Engine") }
                            })
                            addAction(NotificationAction.createSimple("Update Game") {
                                expire()
                                protocol.scheduler.queue { model.installCompanionPlugin.fire("Game") }
                            })
                        }
                        else -> {
                            addAction(NotificationAction.createSimple("Install") {
                                expire()
                                protocol.scheduler.queue { model.installCompanionPlugin.fire("Engine") }
                            })
                        }
                    }
                }
            }
            CompanionPluginStatus.Installed -> {
                Notification(NOTIFICATION_GROUP_ID, "Fathom UE plugin installed", info.message, NotificationType.INFORMATION).apply {
                    addAction(NotificationAction.createSimple("Build Now") {
                        expire()
                        protocol.scheduler.queue { model.buildCompanionPlugin.fire(Unit) }
                    })
                }
            }
            CompanionPluginStatus.UpToDate -> {
                if (info.message.isNotBlank()) {
                    Notification(NOTIFICATION_GROUP_ID, "Fathom UE plugin", info.message, NotificationType.INFORMATION)
                } else null
            }
        }

        if (notification != null) {
            Notifications.Bus.notify(notification, project)
        }
    }
}
