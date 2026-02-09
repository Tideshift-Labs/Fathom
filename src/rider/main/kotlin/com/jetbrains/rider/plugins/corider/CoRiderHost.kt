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
        val model = project.solution.coRiderModel

        // Push persisted port to the backend
        val settings = service<CoRiderSettings>()
        model.port.set(settings.state.port)

        // Create lifetime tied to the project
        val lifetimeDef = LifetimeDefinition()
        @Suppress("IncorrectParentDisposable")
        com.intellij.openapi.util.Disposer.register(project) { lifetimeDef.terminate() }

        // Listen for companion plugin status from backend
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
                        model.installCompanionPlugin.fire(Unit)
                    })
                    Notifications.Bus.notify(notification, project)
                }
                CompanionPluginStatus.UpToDate -> {
                    // Post-install confirmation (backend fires UpToDate after successful install)
                    if (info.message.isNotBlank()) {
                        val notification = group.createNotification(
                            "CoRider UE plugin installed",
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
