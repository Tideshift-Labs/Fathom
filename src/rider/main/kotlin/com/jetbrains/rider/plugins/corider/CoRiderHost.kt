package com.jetbrains.rider.plugins.corider

import com.intellij.notification.NotificationAction
import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.notification.Notifications
import com.intellij.openapi.components.service
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import com.jetbrains.rd.ide.model.CompanionPluginStatus
import com.jetbrains.rd.ide.model.coRiderModel
import com.jetbrains.rd.util.lifetime.LifetimeDefinition
import com.jetbrains.rider.projectView.solution

class CoRiderHost : ProjectActivity {

    override suspend fun execute(project: Project) {
        val solution = project.solution
        val protocol = checkNotNull(solution.protocol) {
            "CoRiderHost: RD protocol not available at postStartupActivity time"
        }
        val model = solution.coRiderModel

        // Push persisted port to the backend (must run on protocol scheduler)
        val settings = service<CoRiderSettings>()
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
                    .getNotificationGroup("CoRider.CompanionPlugin") ?: return@advise
                val notification = group.createNotification(
                    "CoRider MCP configured",
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
                    .getNotificationGroup("CoRider.CompanionPlugin") ?: return@advise

                when (info.status) {
                    CompanionPluginStatus.NotInstalled, CompanionPluginStatus.Outdated -> {
                        val title = if (info.status == CompanionPluginStatus.NotInstalled)
                            "CoRider UE plugin not installed"
                        else
                            "CoRider UE plugin outdated"

                        val notification = group.createNotification(title, info.message, NotificationType.WARNING)
                        notification.addAction(NotificationAction.createSimple("Install") {
                            notification.expire()
                            protocol.scheduler.queue { model.installCompanionPlugin.fire(Unit) }
                        })
                        Notifications.Bus.notify(notification, project)
                    }
                    CompanionPluginStatus.Installed -> {
                        val notification = group.createNotification(
                            "CoRider UE plugin installed",
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
                                "CoRider UE plugin",
                                info.message,
                                NotificationType.INFORMATION
                            )
                            Notifications.Bus.notify(notification, project)
                        }
                    }
                }
            }
        }
    }
}
