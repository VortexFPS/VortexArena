#define _GNU_SOURCE
#include <arpa/inet.h>
#include <dlfcn.h>
#include <stdlib.h>
#include <sys/socket.h>

int bind(int socket, const struct sockaddr *addr, socklen_t length)
{
    int (*real_bind)(int, const struct sockaddr *, socklen_t) = dlsym(RTLD_NEXT, "bind");
    const char *port = getenv("FIXTURE_PORT");
    if (port && addr && addr->sa_family == AF_INET && length >= sizeof(struct sockaddr_in)) {
        struct sockaddr_in local = *(const struct sockaddr_in *)addr;
        if (ntohs(local.sin_port) >= atoi(port) && ntohs(local.sin_port) <= atoi(port) + 8) {
            local.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
            return real_bind(socket, (const struct sockaddr *)&local, sizeof(local));
        }
    }
    if (port && addr && addr->sa_family == AF_INET6 && length >= sizeof(struct sockaddr_in6)) {
        struct sockaddr_in6 local = *(const struct sockaddr_in6 *)addr;
        if (ntohs(local.sin6_port) == atoi(port)) {
            inet_pton(AF_INET6, "::ffff:127.0.0.1", &local.sin6_addr);
            return real_bind(socket, (const struct sockaddr *)&local, sizeof(local));
        }
    }
    return real_bind(socket, addr, length);
}

#include <errno.h>

static int is_local(const struct sockaddr *addr, socklen_t length)
{
    if (!addr)
        return 1;
    if (addr->sa_family == AF_INET && length >= sizeof(struct sockaddr_in))
        return (ntohl(((const struct sockaddr_in *)addr)->sin_addr.s_addr) >> 24) == 127;
    if (addr->sa_family == AF_INET6 && length >= sizeof(struct sockaddr_in6)) {
        const struct in6_addr *ipv6 = &((const struct sockaddr_in6 *)addr)->sin6_addr;
        return IN6_IS_ADDR_LOOPBACK(ipv6) || (IN6_IS_ADDR_V4MAPPED(ipv6) && ipv6->s6_addr[12] == 127);
    }
    return 1;
}

int connect(int socket, const struct sockaddr *addr, socklen_t length)
{
    int (*real_connect)(int, const struct sockaddr *, socklen_t) = dlsym(RTLD_NEXT, "connect");
    if (!is_local(addr, length)) {
        errno = ENETUNREACH;
        return -1;
    }
    return real_connect(socket, addr, length);
}

ssize_t sendto(int socket, const void *data, size_t size, int flags,
               const struct sockaddr *addr, socklen_t length)
{
    ssize_t (*real_sendto)(int, const void *, size_t, int, const struct sockaddr *, socklen_t) =
        dlsym(RTLD_NEXT, "sendto");
    if (!is_local(addr, length)) {
        errno = ENETUNREACH;
        return -1;
    }
    return real_sendto(socket, data, size, flags, addr, length);
}

ssize_t sendmsg(int socket, const struct msghdr *msg, int flags)
{
    ssize_t (*real_sendmsg)(int, const struct msghdr *, int) = dlsym(RTLD_NEXT, "sendmsg");
    if (msg && !is_local(msg->msg_name, msg->msg_namelen)) {
        errno = ENETUNREACH;
        return -1;
    }
    return real_sendmsg(socket, msg, flags);
}
