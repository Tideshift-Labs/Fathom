package com.tideshiftlabs.fathom

import com.intellij.execution.executors.DefaultRunExecutor
import com.intellij.execution.filters.TextConsoleBuilderFactory
import com.intellij.execution.ui.ConsoleView
import com.intellij.execution.ui.ConsoleViewContentType
import com.intellij.execution.ui.RunContentDescriptor
import com.intellij.execution.ui.RunContentManager
import com.intellij.notification.NotificationAction
import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.notification.Notifications
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.service
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import com.jetbrains.rd.ide.model.CompanionPluginStatus
import com.jetbrains.rd.ide.model.fathomModel
import com.jetbrains.rd.util.lifetime.LifetimeDefinition
import com.jetbrains.rider.projectView.solution
import java.util.concurrent.atomic.AtomicReference

class FathomHost : ProjectActivity {

    override suspend fun execute(project: Project) {
        val solution = project.solution
        val protocol = checkNotNull(solution.protocol) {
            "FathomHost: RD protocol not available at postStartupActivity time"
        }
        val model = solution.fathomModel

        // Push persisted port to the backend (must run on protocol scheduler)
        val settings = service<FathomSettings>()
        protocol.scheduler.queue {
            model.port.set(settings.state.port)
        }

        // Create lifetime tied to the project
        val lifetimeDef = LifetimeDefinition()
        @Suppress("IncorrectParentDisposable")
        com.intellij.openapi.util.Disposer.register(project) { lifetimeDef.terminate() }

        // Listen for MCP config provisioning status
        protocol.scheduler.queue {
            model.mcpConfigStatus.advise(lifetimeDef.lifetime) { message ->
                val group = NotificationGroupManager.getInstance()
                    .getNotificationGroup("Fathom.CompanionPlugin") ?: return@advise
                val notification = group.createNotification(
                    "Fathom MCP configured",
                    message,
                    NotificationType.INFORMATION
                )
                Notifications.Bus.notify(notification, project)
            }
        }

        // Listen for companion plugin status from backend (advise must run on protocol scheduler)
        protocol.scheduler.queue {
            model.companionPluginStatus.advise(lifetimeDef.lifetime) { info ->
                val group = NotificationGroupManager.getInstance()
                    .getNotificationGroup("Fathom.CompanionPlugin") ?: return@advise

                when (info.status) {
                    CompanionPluginStatus.NotInstalled -> {
                        val notification = group.createNotification(
                            "Fathom UE plugin not installed",
                            info.message,
                            NotificationType.WARNING
                        )
                        notification.addAction(NotificationAction.createSimple("Install to Engine") {
                            notification.expire()
                            protocol.scheduler.queue { model.installCompanionPlugin.fire("Engine") }
                        })
                        notification.addAction(NotificationAction.createSimple("Install to Game") {
                            notification.expire()
                            protocol.scheduler.queue { model.installCompanionPlugin.fire("Game") }
                        })
                        Notifications.Bus.notify(notification, project)
                    }
                    CompanionPluginStatus.Outdated -> {
                        val notification = group.createNotification(
                            "Fathom UE plugin outdated",
                            info.message,
                            NotificationType.WARNING
                        )
                        // Offer update for wherever it's currently installed
                        when (info.installLocation) {
                            "Engine" -> {
                                notification.addAction(NotificationAction.createSimple("Update") {
                                    notification.expire()
                                    protocol.scheduler.queue { model.installCompanionPlugin.fire("Engine") }
                                })
                            }
                            "Game" -> {
                                notification.addAction(NotificationAction.createSimple("Update") {
                                    notification.expire()
                                    protocol.scheduler.queue { model.installCompanionPlugin.fire("Game") }
                                })
                                notification.addAction(NotificationAction.createSimple("Install to Engine") {
                                    notification.expire()
                                    protocol.scheduler.queue { model.installCompanionPlugin.fire("Engine") }
                                })
                            }
                            "Both" -> {
                                notification.addAction(NotificationAction.createSimple("Update Engine") {
                                    notification.expire()
                                    protocol.scheduler.queue { model.installCompanionPlugin.fire("Engine") }
                                })
                                notification.addAction(NotificationAction.createSimple("Update Game") {
                                    notification.expire()
                                    protocol.scheduler.queue { model.installCompanionPlugin.fire("Game") }
                                })
                            }
                            else -> {
                                notification.addAction(NotificationAction.createSimple("Install") {
                                    notification.expire()
                                    protocol.scheduler.queue { model.installCompanionPlugin.fire("Engine") }
                                })
                            }
                        }
                        Notifications.Bus.notify(notification, project)
                    }
                    CompanionPluginStatus.Installed -> {
                        val notification = group.createNotification(
                            "Fathom UE plugin installed",
                            info.message,
                            NotificationType.INFORMATION
                        )
                        notification.addAction(NotificationAction.createSimple("Build Now") {
                            notification.expire()
                            protocol.scheduler.queue { model.buildCompanionPlugin.fire(Unit) }
                        })
                        Notifications.Bus.notify(notification, project)
                    }
                    CompanionPluginStatus.UpToDate -> {
                        if (info.message.isNotBlank()) {
                            val notification = group.createNotification(
                                "Fathom UE plugin",
                                info.message,
                                NotificationType.INFORMATION
                            )
                            Notifications.Bus.notify(notification, project)
                        }
                    }
                }
            }
        }

        // Build output streaming to Run console
        val consoleRef = AtomicReference<ConsoleView?>(null)

        protocol.scheduler.queue {
            model.companionBuildLog.advise(lifetimeDef.lifetime) { line ->
                ApplicationManager.getApplication().invokeLater {
                    var console = consoleRef.get()
                    if (console == null) {
                        console = TextConsoleBuilderFactory.getInstance()
                            .createBuilder(project)
                            .console
                        consoleRef.set(console)
                        val descriptor = RunContentDescriptor(
                            console, null, console.component, "Fathom UE Build"
                        )
                        RunContentManager.getInstance(project)
                            .showRunContent(DefaultRunExecutor.getRunExecutorInstance(), descriptor)
                    }
                    console.print(line + "\n", ConsoleViewContentType.NORMAL_OUTPUT)
                }
            }

            model.companionBuildFinished.advise(lifetimeDef.lifetime) { success ->
                ApplicationManager.getApplication().invokeLater {
                    val console = consoleRef.getAndSet(null) ?: return@invokeLater
                    if (success) {
                        console.print("\nBuild successful\n", ConsoleViewContentType.SYSTEM_OUTPUT)
                    } else {
                        console.print("\nBuild failed\n", ConsoleViewContentType.ERROR_OUTPUT)
                    }
                }
            }
        }
    }
}
