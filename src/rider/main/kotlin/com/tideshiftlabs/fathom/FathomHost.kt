package com.tideshiftlabs.fathom

import com.intellij.build.BuildViewManager
import com.intellij.build.DefaultBuildDescriptor
import com.intellij.build.events.impl.StartBuildEventImpl
import com.intellij.notification.NotificationAction
import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.notification.Notifications
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.service
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import com.intellij.openapi.wm.ToolWindowManager
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

        // Build output streaming to Build tool window
        val activeBuildId = AtomicReference<Any?>(null)

        protocol.scheduler.queue {
            model.companionBuildLog.advise(lifetimeDef.lifetime) { line ->
                ApplicationManager.getApplication().invokeLater {
                    val buildViewManager = project.getService(BuildViewManager::class.java)
                    var currentBuildId = activeBuildId.get()
                    if (currentBuildId == null) {
                        currentBuildId = Object()
                        activeBuildId.set(currentBuildId)
                        val descriptor = DefaultBuildDescriptor(
                            currentBuildId,
                            "Fathom UE Plugin Build",
                            project.basePath ?: "",
                            System.currentTimeMillis()
                        )
                        buildViewManager.onEvent(
                            currentBuildId,
                            StartBuildEventImpl(
                                null, "Building Fathom UE plugin...", null, null, descriptor, null
                            )
                        )
                        ToolWindowManager.getInstance(project).getToolWindow("Build")?.show()
                    }
                    buildViewManager.onEvent(
                        currentBuildId,
                        FathomOutputBuildEvent(currentBuildId, line + "\n", true)
                    )
                }
            }

            model.companionBuildFinished.advise(lifetimeDef.lifetime) { success ->
                ApplicationManager.getApplication().invokeLater {
                    val currentBuildId = activeBuildId.getAndSet(null) ?: return@invokeLater
                    val buildViewManager = project.getService(BuildViewManager::class.java)
                    val result = if (success) FathomSuccessResult else FathomFailureResult
                    val message = if (success) "Build successful" else "Build failed"
                    buildViewManager.onEvent(
                        currentBuildId,
                        FathomFinishBuildEvent(
                            currentBuildId, null, System.currentTimeMillis(), message, result
                        )
                    )
                }
            }
        }
    }
}
