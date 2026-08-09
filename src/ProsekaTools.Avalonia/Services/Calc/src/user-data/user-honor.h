#ifndef USER_HONOR_H
#define USER_HONOR_H

#include "common/collection-utils.h"

struct UserHonor {
    int honorId = 0;
    int level = 0;

    static inline std::vector<UserHonor> fromJsonList(const json& jsonData) {
        std::vector<UserHonor> userHonors;
        for (const auto& item : jsonData) {
            UserHonor userHonor;
            if (item.is_object()) {
                userHonor.honorId = item.value("honorId", 0);
                userHonor.level = item.value("level", 0);
            } else if (item.is_array()) {
                if (item.size() > 0 && item[0].is_number_integer()) userHonor.honorId = item[0].get<int>();
                if (item.size() > 1 && item[1].is_number_integer()) userHonor.level = item[1].get<int>();
            }
            userHonors.push_back(userHonor);
        }
        return userHonors;
    }
};

#endif // USER_HONOR_H
