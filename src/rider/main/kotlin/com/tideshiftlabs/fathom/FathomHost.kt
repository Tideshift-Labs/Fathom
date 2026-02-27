package com.tideshiftlabs.fathom

import com.intellij.execution.executors.DefaultRunExecutor
import com.intellij.execution.filters.TextConsoleBuilderFactory
import com.intellij.execution.ui.ConsoleView
import com.intellij.execution.ui.ConsoleViewContentType
import com.intellij.execution.ui.RunContentDescriptor
import com.intellij.execution.ui.RunContentManager
import com.intellij.notification.Notification
import com.intellij.notification.NotificationType
import com.intellij.notification.Notifications
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.service
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import com.jetbrains.rd.ide.model.fathomModel
import com.jetbrains.rd.util.lifetime.LifetimeDefinition
import com.jetbrains.rider.projectView.solution
import java.util.concurrent.atomic.AtomicReference

class FathomHost : ProjectActivity {

    companion object {
        private const val GROUP_ID = "Fathom.CompanionPlugin"
    }

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
                ApplicationManager.getApplication().invokeLater {
                    val notification = Notification(GROUP_ID, "Fathom MCP configured", message, NotificationType.INFORMATION)
                    Notifications.Bus.notify(notification, project)
                }
            }
        }

        // NOTE: Companion plugin status notifications are handled in
        // FathomStatusBarWidget.install(), which runs earlier than this
        // PostStartupActivity. The RD sink fires before execute() runs,
        // so an advise registered here would miss the event.

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
